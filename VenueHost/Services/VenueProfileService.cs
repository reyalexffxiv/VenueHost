using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using VenueHost.Models;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VenueHost.Services;

/// <summary>
/// Independent venue configurations with one shared DJ/Staff directory.
/// Switching only changes venue-owned fields; shared people never disappear.
/// </summary>
public sealed class VenueProfileService
{
    private const int ArchiveVersion = 2;
    private const long MaxArchiveBytes = 8 * 1024 * 1024;
    private readonly Configuration config;
    private readonly string directory;
    private readonly string indexPath;
    private readonly string sharedDirectoryPath;
    private readonly string sqliteDbPath;
    private readonly string recoveryDirectory;
    private readonly Action stopBeforeSwitch;
    private readonly Action refreshAfterSwitch;
    private readonly List<VenueProfileInfo> profiles = new();
    private bool applying;
    private string activeId = string.Empty;

    private static readonly HashSet<string> ExcludedFields = new(StringComparer.Ordinal)
    {
        nameof(Configuration.Version), nameof(Configuration.ContrastModeEnabled),
        nameof(Configuration.OverviewHighContrastPreviewEnabled),
        nameof(Configuration.ShowChatSenderFallbackHelper),
        nameof(Configuration.CleanLegacyManualWaitLinesOnceDone),
        nameof(Configuration.DjDatabase), nameof(Configuration.StaffDatabase),
    };

    public VenueProfileService(Configuration config, string configDirectory,
        Action stopBeforeSwitch, Action refreshAfterSwitch)
    {
        this.config = config;
        this.stopBeforeSwitch = stopBeforeSwitch;
        this.refreshAfterSwitch = refreshAfterSwitch;
        this.directory = Path.Combine(configDirectory, "venue_profiles");
        this.recoveryDirectory = Path.Combine(configDirectory, "venue_profile_recovery");
        this.indexPath = Path.Combine(configDirectory, "venue_profiles_index.json");
        this.sharedDirectoryPath = Path.Combine(configDirectory, "venue_shared_people.json");
        this.sqliteDbPath = Path.Combine(configDirectory, "dj_database.sqlite3");
        Directory.CreateDirectory(this.directory);
        Directory.CreateDirectory(this.recoveryDirectory);
        this.LoadIndexOrCreateFirstProfile();
    }

    public IReadOnlyList<VenueProfileInfo> Profiles => this.profiles;
    public string ActiveId => this.activeId;
    public VenueProfileInfo Active => this.profiles.First(p => p.Id == this.activeId);
    public string RecoveryDirectory => this.recoveryDirectory;

    /// <summary>Called whenever the active configuration is saved by existing UI/services.</summary>
    public void CaptureActive()
    {
        if (this.applying || this.profiles.Count == 0) return;
        this.RepairActiveScheduleReferences();
        this.SaveSharedPeople();
        this.SaveSnapshot(this.Active, this.ExtractData());
    }

    public void Rename(string id, string newName)
    {
        var profile = this.Find(id);
        profile.Name = CleanName(newName);
        this.SaveIndex();
        if (id == this.activeId) this.CaptureActive();
    }

    public VenueProfileInfo CreateEmpty(string name)
    {
        // Creating a profile must not alter the current one.
        var created = new VenueProfileInfo { Id = Guid.NewGuid().ToString("N"), Name = CleanName(name) };
        var blank = new Configuration
        {
            DjSchedule = [], StaffSchedule = [], EventShouts = [],
        };
        var data = ExtractData(blank);
        this.SaveSnapshot(created, data);
        this.profiles.Add(created);
        this.SaveIndex();
        return created;
    }

    public void Delete(string id)
    {
        if (this.profiles.Count <= 1) throw new InvalidOperationException("Keep at least one venue profile.");
        if (id == this.activeId) throw new InvalidOperationException("Switch to another venue before deleting this one.");
        var profile = this.Find(id);
        // A deletion is also recoverable by restoring its saved snapshot.
        var path = this.ProfilePath(profile.Id);
        if (File.Exists(path))
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            File.Copy(path, Path.Combine(this.recoveryDirectory,
                $"deleted_{stamp}_{profile.Id}.venuehost"), true);
        }
        this.profiles.Remove(profile);
        this.SaveIndex();
        if (File.Exists(path)) File.Delete(path);
    }

    public void Switch(string id)
    {
        if (id == this.activeId) return;
        var target = this.Find(id);
        var data = this.ReadSnapshot(target);
        this.SwitchToData(data, target.Id);
    }

    /// <summary>Exports the SELECTED venue and complete shared people directory.</summary>
    public void ExportSelected(string id, string path)
    {
        var profile = this.Find(id);
        if (id == this.activeId) this.CaptureActive();
        var data = id == this.activeId ? this.ExtractData() : this.ReadSnapshot(profile);
        WriteAtomically(path, SerializeArchive(profile.Name, data, this.PeopleData()));
    }

    public void ExportActive(string path) => this.ExportSelected(this.activeId, path);

    public ProfileImportPreview InspectImport(string path)
    {
        var archive = ReadArchive(path);
        var data = archive.Data!;
        var people = GetArchivePeople(archive);
        var incomingDjs = ReadDjs(people);
        var incomingStaff = ReadStaff(people);
        return new ProfileImportPreview
        {
            Name = CleanName(archive.Name ?? "Imported Venue"),
            DjCount = incomingDjs.Count,
            DjSlotCount = (data[nameof(Configuration.DjSchedule)] as JArray)?.Count ?? 0,
            StaffCount = incomingStaff.Count,
            ShiftCount = (data["Photographers"] as JArray)?.Count ?? 0,
            RoleCount = (data[nameof(Configuration.StaffRoles)] as JArray)?.Count ?? 0,
            ShoutCount = (data[nameof(Configuration.EventShouts)] as JArray)?.Count ?? 0,
            ConflictingDjs = incomingDjs.Count(i => this.config.DjDatabase.Any(e => Key(e.Name) == Key(i.Name) && e.Link != i.Link)),
            ConflictingStaff = incomingStaff.Count(i => this.config.StaffDatabase.Any(e => Key(e.Name) == Key(i.Name) && (e.Role != i.Role || e.Link != i.Link))),
        };
    }

    /// <summary>Default import is non-destructive: a new independent profile is created.</summary>
    public VenueProfileInfo ImportNew(string path, bool updateExistingContacts = false)
    {
        var archive = ReadArchive(path);
        var profile = new VenueProfileInfo
        {
            Id = Guid.NewGuid().ToString("N"), Name = CleanName(archive.Name ?? "Imported Venue"),
        };
        // Import can update the global directory: stop pending announcements first.
        this.stopBeforeSwitch();
        this.SaveSnapshot(profile, archive.Data!);
        try
        {
            this.MergeImportedPeople(archive, updateExistingContacts);
            this.profiles.Add(profile);
            this.SaveIndex();
            return profile;
        }
        catch
        {
            // A failed import must not leave an unindexed profile archive.
            var orphan = this.ProfilePath(profile.Id);
            if (File.Exists(orphan)) File.Delete(orphan);
            throw;
        }
    }

    /// <summary>Explicit replace only, with a recoverable copy of the previous profile.</summary>
    public void ReplaceActiveFromImport(string path, bool updateExistingContacts = false)
    {
        var archive = ReadArchive(path);
        this.stopBeforeSwitch();
        this.CreateRecoveryCopy();
        this.MergeImportedPeople(archive, updateExistingContacts);
        this.SwitchToData(archive.Data!, this.activeId);
    }

    private void SwitchToData(JObject data, string targetId)
    {
        // Validate and deserialize ALL fields before mutating any live state.
        var values = DeserializeProperties(data);
        var oldValues = DeserializeProperties(this.ExtractData());
        var previousId = this.activeId;
        this.CaptureActive();
        this.stopBeforeSwitch(); // Cancel both automatic and manual pending commands.
        this.applying = true;
        try
        {
            this.config.ShoutPriority = [];
            this.config.SmartChatChannelsEnabled = true;
            ApplyProperties(this.config, values);
            // The DJ SQLite store and the shared staff directory are global.
            // Switching venues must never replace or rewrite them.
            this.RepairActiveScheduleReferences();
            this.config.EnsureStaffRoles();
            this.config.EnsureStaffRoleShoutSettings();
            this.config.EnsureEventShoutDefaults();
            this.config.NormalizeAnnouncementMacros();
            this.config.EnsureShoutPriority();
            this.config.ClampAutoShoutSettings();
            this.activeId = targetId;
            this.SaveIndex();
            this.config.Save();
        }
        catch
        {
            // Best-effort in-memory and SQLite rollback on disk/serialization failure.
            ApplyProperties(this.config, oldValues);
            this.activeId = previousId;
            this.SaveIndex();
            this.config.Save();
            throw;
        }
        finally
        {
            this.applying = false;
        }
        this.CaptureActive();
        this.refreshAfterSwitch();
    }

    private void CreateRecoveryCopy()
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        var path = Path.Combine(this.recoveryDirectory, $"{stamp}_{this.activeId}.venuehost");
        WriteAtomically(path, SerializeArchive(this.Active.Name, this.ExtractData(), this.PeopleData()));
    }

    private void LoadIndexOrCreateFirstProfile()
    {
        if (File.Exists(this.indexPath))
        {
            var index = JsonConvert.DeserializeObject<ProfileIndex>(File.ReadAllText(this.indexPath));
            if (index is null || index.Version != 1 || index.Profiles is null ||
                index.Profiles.Count == 0 || index.Profiles.Count > 500 ||
                index.Profiles.Any(p => p is null || !Guid.TryParseExact(p.Id, "N", out _) ||
                    string.IsNullOrWhiteSpace(p.Name)) ||
                index.Profiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != index.Profiles.Count ||
                !index.Profiles.Any(p => p.Id == index.ActiveId))
                throw new InvalidDataException("Invalid venue profiles index; original files were left untouched.");
            this.profiles.AddRange(index.Profiles);
            this.activeId = index.ActiveId!;
            this.LoadOrMigrateSharedPeople();
            // The index and its active snapshot are authoritative after a crash,
            // even if Dalamud's config or the active SQLite working copy was
            // partially updated during a venue switch. Never overwrite a known
            // good snapshot with potentially mixed in-memory data on startup.
            var saved = DeserializeProperties(this.ReadSnapshot(this.Active));
            this.applying = true;
            try
            {
                this.config.ShoutPriority = [];
                this.config.SmartChatChannelsEnabled = true;
                ApplyProperties(this.config, saved);
                this.RepairActiveScheduleReferences();
                this.config.EnsureStaffRoles();
                this.config.EnsureStaffRoleShoutSettings();
                this.config.EnsureEventShoutDefaults();
                this.config.NormalizeAnnouncementMacros();
                this.config.ShoutPriority ??= [];
                this.config.EnsureShoutPriority();
                this.config.ClampAutoShoutSettings();
                this.config.Save();
            }
            finally
            {
                this.applying = false;
            }
            return;
        }
        this.LoadOrMigrateSharedPeople();
        var first = new VenueProfileInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(this.config.VenueName) ? "My Venue" : CleanName(this.config.VenueName),
        };
        this.profiles.Add(first);
        this.activeId = first.Id;
        this.SaveSnapshot(first, this.ExtractData());
        this.SaveIndex();
    }

    private VenueProfileInfo Find(string id) => this.profiles.FirstOrDefault(p => p.Id == id)
        ?? throw new InvalidOperationException("Venue profile not found.");

    private string ProfilePath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid profile ID.");
        return Path.Combine(this.directory, id + ".venuehost");
    }

    private void SaveIndex() => WriteAtomically(this.indexPath, JsonConvert.SerializeObject(new ProfileIndex
    {
        ActiveId = this.activeId, Profiles = this.profiles,
    }, Formatting.Indented));

    private void SaveSnapshot(VenueProfileInfo profile, JObject data)
        => WriteAtomically(this.ProfilePath(profile.Id), SerializeArchive(profile.Name, WithoutPeople(data)));

    private JObject ReadSnapshot(VenueProfileInfo profile) => ReadArchive(this.ProfilePath(profile.Id)).Data!;

    private JObject ExtractData() => ExtractData(this.config);

    private static JObject ExtractData(Configuration source)
    {
        var serialized = JObject.FromObject(source);
        foreach (var excluded in ExcludedFields) serialized.Remove(excluded);
        return serialized;
    }

    private static Dictionary<PropertyInfo, object?> DeserializeProperties(JObject data)
    {
        // Never allow arbitrary CLR type annotations from imported JSON.
        var properties = new Dictionary<PropertyInfo, object?>();
        foreach (var property in typeof(Configuration).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || ExcludedFields.Contains(property.Name)) continue;
            var name = property.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName ?? property.Name;
            var token = data[name];
            if (token is null) continue;
            properties[property] = token.ToObject(property.PropertyType);
        }
        var required = new[]
        {
            nameof(Configuration.DjSchedule), nameof(Configuration.StaffSchedule),
            nameof(Configuration.StaffRoles), nameof(Configuration.StaffRoleShoutSettings),
            nameof(Configuration.EventShouts), nameof(Configuration.VenueName),
            nameof(Configuration.EventStartDate), nameof(Configuration.EventStartTime),
            nameof(Configuration.EventEndDate), nameof(Configuration.EventEndTime),
            nameof(Configuration.CurrentDjMacro), nameof(Configuration.TransitionMacro),
            nameof(Configuration.AutoCurrentDjShoutIntervalMinutes),
        };
        if (required.Any(key => !properties.Any(p => p.Key.Name == key)))
            throw new InvalidDataException("The venue profile is missing required schedule, role, or macro data.");
        return properties;
    }

    private static void ApplyProperties(Configuration target, Dictionary<PropertyInfo, object?> properties)
    {
        foreach (var pair in properties)
            pair.Key.SetValue(target, pair.Value);
    }

    private static string SerializeArchive(string name, JObject data, JObject? people = null)
    {
        var venueOnly = WithoutPeople(data);
        // Checksums cover the shared directory too, if this is an exported archive.
        var canonical = venueOnly.ToString(Formatting.None) + (people?.ToString(Formatting.None) ?? string.Empty);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return JsonConvert.SerializeObject(new ProfileArchive
        {
            Format = "VenueHost-Profile", FormatVersion = ArchiveVersion,
            Name = name, Data = venueOnly, People = people, Sha256 = digest,
        }, Formatting.Indented);
    }

    private static ProfileArchive ReadArchive(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > MaxArchiveBytes || file.Length == 0)
            throw new InvalidDataException("Profile is missing, empty, or exceeds the 8 MB limit.");
        var archive = JsonConvert.DeserializeObject<ProfileArchive>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid venue profile file.");
        if (archive.Format != "VenueHost-Profile" || (archive.FormatVersion != 1 && archive.FormatVersion != 2) || archive.Data is null)
            throw new InvalidDataException("Unsupported VenueHost profile format or version.");
        var canonical = archive.FormatVersion == 1
            ? archive.Data.ToString(Formatting.None)
            : WithoutPeople(archive.Data).ToString(Formatting.None) + (archive.People?.ToString(Formatting.None) ?? string.Empty);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        if (!string.Equals(digest, archive.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Profile checksum mismatch. File may be damaged.");
        DeserializeProperties(archive.Data); // Validate before offering import.
        if (archive.People is not null)
        {
            _ = ReadDjs(archive.People);
            _ = ReadStaff(archive.People);
        }
        return archive;
    }

    private static void WriteAtomically(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    /// <summary>Count shifts/slots referencing a person across ALL venue profiles.</summary>
    public int CountReferences(string name, bool dj)
    {
        var count = 0;
        foreach (var profile in this.profiles)
        {
            if (profile.Id == this.activeId)
            {
                count += dj
                    ? this.config.DjSchedule.Count(x => Key(x.DJName) == Key(name))
                    : this.config.StaffSchedule.Count(x => Key(x.Name) == Key(name));
                continue;
            }
            var data = this.ReadSnapshot(profile);
            var rows = data[dj ? nameof(Configuration.DjSchedule) : "Photographers"] as JArray;
            var key = dj ? "DJName" : "Name";
            if (rows is not null)
                count += rows.OfType<JObject>().Count(row => Key(row.Value<string>(key)) == Key(name));
        }
        return count;
    }

    /// <summary>Renaming a shared person updates all venue schedules, even inactive ones.</summary>
    public void RenameSharedPerson(string previousName, string newName, bool dj)
    {
        if (Key(previousName) == Key(newName) && previousName == newName) return;
        foreach (var profile in this.profiles)
        {
            if (profile.Id == this.activeId)
            {
                if (dj)
                {
                    var entry = this.config.DjDatabase.FirstOrDefault(x => Key(x.Name) == Key(newName));
                    foreach (var slot in this.config.DjSchedule.Where(x => Key(x.DJName) == Key(previousName)))
                    {
                        slot.DJName = newName;
                        if (entry is not null) { slot.DJId = entry.Id; slot.DJLink = entry.Link; }
                    }
                }
                else
                {
                    var entry = this.config.StaffDatabase.FirstOrDefault(x => Key(x.Name) == Key(newName));
                    foreach (var shift in this.config.StaffSchedule.Where(x => Key(x.Name) == Key(previousName)))
                    {
                        shift.Name = newName;
                        if (entry is not null) shift.PersonId = entry.Id;
                    }
                }
                continue;
            }
            var data = this.ReadSnapshot(profile);
            var rows = data[dj ? nameof(Configuration.DjSchedule) : "Photographers"] as JArray;
            if (rows is null) continue;
            foreach (var row in rows.OfType<JObject>())
            {
                var key = dj ? "DJName" : "Name";
                if (Key(row.Value<string>(key)) != Key(previousName)) continue;
                row[key] = newName;
                if (dj)
                {
                    var entry = this.config.DjDatabase.FirstOrDefault(x => Key(x.Name) == Key(newName));
                    if (entry is not null) { row["DJId"] = entry.Id; row["DJLink"] = entry.Link; }
                }
                else
                {
                    var entry = this.config.StaffDatabase.FirstOrDefault(x => Key(x.Name) == Key(newName));
                    if (entry is not null) row["PersonId"] = entry.Id;
                }
            }
            this.SaveSnapshot(profile, data);
        }
        this.config.Save();
    }

    private void RepairActiveScheduleReferences()
    {
        foreach (var slot in this.config.DjSchedule ?? [])
        {
            var entry = this.config.DjDatabase.FirstOrDefault(x =>
                (!string.IsNullOrEmpty(slot.DJId) && x.Id == slot.DJId) || Key(x.Name) == Key(slot.DJName));
            if (entry is null) continue;
            slot.DJId = entry.Id;
            slot.DJName = entry.Name;
            if (string.IsNullOrWhiteSpace(slot.DJLink)) slot.DJLink = entry.Link;
        }
        foreach (var shift in this.config.StaffSchedule ?? [])
        {
            var entry = this.config.StaffDatabase.FirstOrDefault(x =>
                (!string.IsNullOrEmpty(shift.PersonId) && x.Id == shift.PersonId) || Key(x.Name) == Key(shift.Name));
            if (entry is null) continue;
            shift.PersonId = entry.Id;
            shift.Name = entry.Name;
        }
    }

    // Global people are stored independently of profiles. Legacy v1 snapshots
    // are read once and their contacts merged BEFORE loading any venue snapshot.
    private void LoadOrMigrateSharedPeople()
    {
        if (File.Exists(this.sharedDirectoryPath))
        {
            var envelope = JsonConvert.DeserializeObject<SharedPeopleArchive>(File.ReadAllText(this.sharedDirectoryPath))
                ?? throw new InvalidDataException("Invalid shared people directory; files were left unchanged.");
            var canonical = envelope.People?.ToString(Formatting.None) ?? string.Empty;
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            if (envelope.Version != 1 || !string.Equals(digest, envelope.Sha256, StringComparison.OrdinalIgnoreCase) || envelope.People is null)
                throw new InvalidDataException("Shared people directory checksum error; profiles were not loaded.");
            this.config.DjDatabase = ReadDjs(envelope.People);
            this.config.StaffDatabase = ReadStaff(envelope.People);
            this.config.NormalizeDjDatabase(saveToStore: true);
            return;
        }

        // All original profile archives, the index, and the original active JSON
        // survive migration. This permits a full manual rollback to v0.8.6.
        var backupFolder = Path.Combine(this.recoveryDirectory,
            "before_shared_people_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(backupFolder);
        if (File.Exists(this.indexPath)) File.Copy(this.indexPath, Path.Combine(backupFolder, "venue_profiles_index.json"));
        foreach (var filename in Directory.GetFiles(this.directory, "*.venuehost"))
            File.Copy(filename, Path.Combine(backupFolder, Path.GetFileName(filename)));
        if (File.Exists(this.sqliteDbPath))
        {
            // SQLite may be in WAL mode. This file is supplementary only;
            // the active config and legacy snapshots are the migration sources.
            File.Copy(this.sqliteDbPath, Path.Combine(backupFolder, "dj_database.sqlite3"));
        }
        var djs = new List<DjDatabaseEntry>(this.config.DjDatabase ?? []);
        var staff = new List<StaffDatabaseEntry>(this.config.StaffDatabase ?? []);
        foreach (var filename in Directory.GetFiles(this.directory, "*.venuehost"))
        {
            var archive = ReadArchive(filename);
            var people = GetArchivePeople(archive);
            djs.AddRange(ReadDjs(people));
            staff.AddRange(ReadStaff(people));
        }
        this.config.DjDatabase = MergeDjs([], djs, false);
        this.config.StaffDatabase = MergeStaff([], staff, false);
        this.RepairActiveScheduleReferences();
        this.SaveSharedPeople();
        this.config.NormalizeDjDatabase(saveToStore: true);
    }

    private JObject PeopleData() => new()
    {
        [nameof(Configuration.DjDatabase)] = JArray.FromObject(this.config.DjDatabase ?? []),
        [nameof(Configuration.StaffDatabase)] = JArray.FromObject(this.config.StaffDatabase ?? []),
    };

    private static JObject WithoutPeople(JObject data)
    {
        var clone = (JObject)data.DeepClone();
        clone.Remove(nameof(Configuration.DjDatabase));
        clone.Remove(nameof(Configuration.StaffDatabase));
        return clone;
    }

    private static JObject GetArchivePeople(ProfileArchive archive)
        => archive.People ?? archive.Data!; // v1 stored contacts in Data.

    private static string Key(string? name)
        => Regex.Replace((name ?? string.Empty).Trim(), @"\s+", " ").ToUpperInvariant();

    private static List<DjDatabaseEntry> ReadDjs(JObject people)
        => (people[nameof(Configuration.DjDatabase)] as JArray)?.ToObject<List<DjDatabaseEntry>>() ?? [];

    private static List<StaffDatabaseEntry> ReadStaff(JObject people)
        => (people[nameof(Configuration.StaffDatabase)] as JArray)?.ToObject<List<StaffDatabaseEntry>>() ?? [];

    private static List<DjDatabaseEntry> MergeDjs(IEnumerable<DjDatabaseEntry> existing,
        IEnumerable<DjDatabaseEntry> incoming, bool updateExisting)
    {
        var result = new Dictionary<string, DjDatabaseEntry>(StringComparer.Ordinal);
        foreach (var e in existing.Concat(incoming))
        {
            if (e is null || string.IsNullOrWhiteSpace(e.Name)) continue;
            var key = Key(e.Name);
            var cleanName = Regex.Replace(e.Name.Trim(), @"\s+", " ");
            var cleanLink = (e.Link ?? string.Empty).Trim();
            if (!result.TryGetValue(key, out var old))
                result[key] = new DjDatabaseEntry { Id = string.IsNullOrWhiteSpace(e.Id) ? Guid.NewGuid().ToString("N") : e.Id, Name = cleanName, Link = cleanLink };
            else if ((updateExisting || string.IsNullOrWhiteSpace(old.Link)) && !string.IsNullOrWhiteSpace(cleanLink))
                old.Link = cleanLink;
        }
        return result.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<StaffDatabaseEntry> MergeStaff(IEnumerable<StaffDatabaseEntry> existing,
        IEnumerable<StaffDatabaseEntry> incoming, bool updateExisting)
    {
        var result = new Dictionary<string, StaffDatabaseEntry>(StringComparer.Ordinal);
        foreach (var e in existing.Concat(incoming))
        {
            if (e is null || string.IsNullOrWhiteSpace(e.Name)) continue;
            var key = Key(e.Name);
            var cleanName = Regex.Replace(e.Name.Trim(), @"\s+", " ");
            if (!result.TryGetValue(key, out var old))
                result[key] = new StaffDatabaseEntry { Id = string.IsNullOrWhiteSpace(e.Id) ? Guid.NewGuid().ToString("N") : e.Id, Name = cleanName, Link = (e.Link ?? string.Empty).Trim(), Role = e.Role ?? "Photographer" };
            else if (updateExisting)
            {
                old.Link = (e.Link ?? string.Empty).Trim();
                old.Role = e.Role ?? "Photographer";
            }
        }
        return result.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void MergeImportedPeople(ProfileArchive archive, bool updateExisting)
    {
        var people = GetArchivePeople(archive);
        var djs = MergeDjs(this.config.DjDatabase, ReadDjs(people), updateExisting);
        var staff = MergeStaff(this.config.StaffDatabase, ReadStaff(people), updateExisting);
        // A recovery copy includes the global people directory as well.
        this.CreateRecoveryCopy();
        this.config.DjDatabase = djs;
        this.config.StaffDatabase = staff;
        this.config.NormalizeDjDatabase(saveToStore: true);
        this.SaveSharedPeople();
        this.config.Save();
    }

    private void SaveSharedPeople()
    {
        // A separately checksummed, atomic file is authoritative on restart.
        var people = this.PeopleData();
        var canonical = people.ToString(Formatting.None);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        WriteAtomically(this.sharedDirectoryPath, JsonConvert.SerializeObject(
            new SharedPeopleArchive { Version = 1, People = people, Sha256 = hash }, Formatting.Indented));
    }

    private sealed class SharedPeopleArchive
    {
        public int Version { get; set; }
        public JObject? People { get; set; }
        public string? Sha256 { get; set; }
    }

    private static string CleanName(string name)
    {
        var cleaned = (name ?? string.Empty).Trim();
        if (cleaned.Length == 0 || cleaned.Length > 80 || cleaned.Any(char.IsControl))
            throw new ArgumentException("Choose a venue name between 1 and 80 characters.");
        return cleaned;
    }

    private sealed class ProfileIndex
    {
        public int Version { get; set; } = 1;
        public string? ActiveId { get; set; }
        public List<VenueProfileInfo>? Profiles { get; set; }
    }

    private sealed class ProfileArchive
    {
        public string Format { get; set; } = string.Empty;
        public int FormatVersion { get; set; }
        public string? Name { get; set; }
        public JObject? Data { get; set; }
        public JObject? People { get; set; }
        public string? Sha256 { get; set; }
    }
}

public sealed class VenueProfileInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class ProfileImportPreview
{
    public string Name { get; set; } = string.Empty;
    public int DjCount { get; set; }
    public int DjSlotCount { get; set; }
    public int StaffCount { get; set; }
    public int ConflictingDjs { get; set; }
    public int ConflictingStaff { get; set; }
    public int ShiftCount { get; set; }
    public int RoleCount { get; set; }
    public int ShoutCount { get; set; }
}
