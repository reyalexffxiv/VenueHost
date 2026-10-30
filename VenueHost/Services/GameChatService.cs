using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using Dalamud.Plugin.Services;

namespace VenueHost.Services;

/// <summary>
/// Executes native game chatbox commands.
///
/// IChatGui.Print only writes local plugin/system messages.
/// It does not send public chat messages.
///
/// For commands like /s, /sh, /emote, etc., we use the game's
/// RaptureShellModule command path through FFXIVClientStructs.
/// Also spaces queued commands so FFXIV does not reject rapid macro lines.
/// </summary>
public sealed class GameChatService
{
    private readonly IPluginLog log;
    private readonly Queue<QueuedChatCommand> pendingCommands = new();

    private DateTime nextCommandAllowedAt = DateTime.MinValue;
    // True when the most recently observed Yell/Shout was a Yell.
    // Null means no such chat activity has been observed in this session.
    private bool? lastWasYell;


    public GameChatService(IPluginLog log)
    {
        this.log = log;
        this.LastStatus = "Native chat sender ready.";
    }

    /// <summary>
    /// Last visible status, shown in the VenueHost UI.
    /// </summary>
    public string LastStatus { get; private set; }

    public int PendingCommandCount => this.pendingCommands.Count;

    public bool HasPendingCommands => this.pendingCommands.Count > 0;
    public bool SmartChannelsEnabled { get; set; } = true;

    /// <summary>Record only observed public Yell/Shout activity; no message content is retained.</summary>
    public void ObserveChatChannel(Dalamud.Game.Text.XivChatType kind)
    {
        if (kind == Dalamud.Game.Text.XivChatType.Yell) this.lastWasYell = true;
        else if (kind == Dalamud.Game.Text.XivChatType.Shout) this.lastWasYell = false;
    }

    /// <summary>Pause gate for automatic dispatch only. Explicit manual announcements remain allowed.</summary>
    public bool DispatchPaused { get; private set; }

    /// <summary>
    /// Stops automatic dispatch and cancels pending automatic commands, including
    /// remaining lines of automatic macros. Never discard a manually queued shout.
    /// </summary>
    public int PauseAndCancelPending()
    {
        this.DispatchPaused = true;
        var removed = 0;
        var pendingCount = this.pendingCommands.Count;
        for (var i = 0; i < pendingCount; i++)
        {
            var queued = this.pendingCommands.Dequeue();
            if (queued.IsManual)
                this.pendingCommands.Enqueue(queued);
            else
                removed++;
        }
        this.LastStatus = $"Automation paused. Cancelled {removed} pending automatic commands.";
        return removed;
    }

    /// <summary>Cancel only queued automatic lines for a disabled staff role.</summary>
    public void CancelPendingAutomaticStaffRole(string role)
    {
        var count = this.pendingCommands.Count;
        for (var i = 0; i < count; i++)
        {
            var command = this.pendingCommands.Dequeue();
            if (command.IsManual || !string.Equals(command.StaffRole, role, StringComparison.OrdinalIgnoreCase))
                this.pendingCommands.Enqueue(command);
        }
    }

    /// <summary>Profile changes must never leak either manual or automatic messages between venues.</summary>
    public void ClearAllForProfileSwitch()
    {
        this.pendingCommands.Clear();
        this.nextCommandAllowedAt = DateTime.MinValue;
        this.DispatchPaused = true;
        this.LastStatus = "Venue changed. All pending chat commands cleared; automation paused.";
    }

    public void ResumeDispatch()
    {
        this.DispatchPaused = false;
        this.LastStatus = "Native chat dispatcher resumed.";
    }

    /// <summary>Last command that failed to execute, if any. Useful for manual copy/paste fallback.</summary>
    public string? LastFailedCommand { get; private set; }

    /// <summary>Time of the last native chat sender failure.</summary>
    public DateTime? LastFailureAt { get; private set; }

    public bool HasFailedCommand => !string.IsNullOrWhiteSpace(this.LastFailedCommand);

    /// <summary>
    /// Queues a /s message.
    /// </summary>
    public void QueueSay(string message)
    {
        this.QueueChatMessage("/s", message);
    }

    /// <summary>
    /// Queues a /sh message.
    /// </summary>
    public void QueueShout(string message)
    {
        this.QueueChatMessage("/sh", message);
    }

    /// <summary>
    /// Queues a /yell message.
    /// </summary>
    public void QueueYell(string message)
    {
        this.QueueChatMessage("/y", message);
    }

    /// <summary>
    /// Queues a /yell message and waits after it before the next queued command.
    /// </summary>
    public void QueueYellWithDelay(string message, TimeSpan delayAfterCommand)
    {
        this.QueueChatMessage("/y", message, delayAfterCommand);
    }

    /// <summary>
    /// Queues a /tell message to the current in-game target.
    /// FFXIV resolves the &lt;t&gt; placeholder, which avoids problems with player names that contain spaces.
    /// The user must still have the intended buyer targeted when the message is sent.
    /// </summary>
    public void QueueTellCurrentTarget(string message)
    {
        this.QueueChatMessage("/tell <t>", message);
    }

    /// <summary>
    /// Queues several /sh messages with a delay between each one.
    /// This is safer than trying to inject macro-style &lt;wait.2&gt; text.
    /// </summary>
    public void QueueShoutSequence(IEnumerable<string> messages, TimeSpan delayBetweenMessages)
    {
        foreach (var message in messages)
            this.QueueChatMessage("/sh", message, delayBetweenMessages);

        this.LastStatus = "Queued shout sequence.";
    }


    /// <summary>
    /// Queues a user-editable macro.
    /// Each text line becomes a Yell/Shout. Explicit non-channel commands remain unchanged.
    /// The default delay is applied after every command line.
    /// Optional lines like &lt;wait.3&gt; still work and override the delay after the previous command.
    /// </summary>
    public void QueueMacroCommands(string macroText, TimeSpan defaultDelayAfterEachCommand, string? staffRole = null)
    {
        this.QueueMacroCommandsCore(macroText, defaultDelayAfterEachCommand, isManual: false, staffRole: staffRole);
    }

    /// <summary>
    /// Queue an explicit user-triggered macro. This works even while automatic
    /// announcements are paused, and its queued lines survive PAUSE ALL.
    /// </summary>
    public void QueueManualMacroCommands(string macroText, TimeSpan defaultDelayAfterEachCommand)
    {
        this.QueueMacroCommandsCore(macroText, defaultDelayAfterEachCommand, isManual: true);
    }

    private void QueueMacroCommandsCore(string macroText, TimeSpan defaultDelayAfterEachCommand, bool isManual, string? staffRole = null)
    {
        if (this.DispatchPaused && !isManual)
        {
            this.LastStatus = "Automation paused: automatic macro not queued.";
            return;
        }
        if (string.IsNullOrWhiteSpace(macroText))
        {
            this.LastStatus = "Not queued: macro was empty.";
            return;
        }

        if (defaultDelayAfterEachCommand < TimeSpan.Zero)
            defaultDelayAfterEachCommand = TimeSpan.Zero;

        if (defaultDelayAfterEachCommand > TimeSpan.FromSeconds(60))
            defaultDelayAfterEachCommand = TimeSpan.FromSeconds(60);

        string? pendingCommand = null;
        var pendingDelay = defaultDelayAfterEachCommand;
        var queuedCount = 0;

        foreach (var rawLine in macroText.Replace("\r", string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var wait = TryParseWaitLine(line);
            if (wait.HasValue)
            {
                if (pendingCommand is not null)
                    pendingDelay = wait.Value;

                continue;
            }

            if (pendingCommand is not null)
            {
                this.QueueCommand(pendingCommand, pendingDelay, isManual, staffRole);
                queuedCount++;
            }

            // Legacy channel prefixes are recognized at dispatch without changing
            // an open editor draft; plain announcement lines need no slash command.
            pendingCommand = Configuration.NormalizeAnnouncementMacro(line);
            pendingDelay = defaultDelayAfterEachCommand;
        }

        if (pendingCommand is not null)
        {
            this.QueueCommand(pendingCommand, pendingDelay, isManual, staffRole);
            queuedCount++;
        }

        this.LastStatus = $"Queued macro commands: {queuedCount}.";
    }

    /// <summary>
    /// Queues a user-editable macro without an automatic delay.
    /// </summary>
    public void QueueMacroCommands(string macroText)
    {
        this.QueueMacroCommands(macroText, TimeSpan.Zero);
    }

    /// <summary>
    /// Adds one already-expanded FFXIV chat command to the queue.
    /// </summary>
    public void QueueCommand(string command)
    {
        this.QueueCommand(command, TimeSpan.Zero);
    }

    /// <summary>
    /// Queues a harmless local echo command so users can test the native sender after patches.
    /// </summary>
    public void QueueSenderTestCommand()
    {
        this.QueueCommand($"/e Venue Host chat sender test {DateTime.Now:HH:mm:ss}",
            TimeSpan.Zero, isManual: true);
    }

    /// <summary>Clears the stored failed command after the user has copied or acknowledged it.</summary>
    public void ClearFailedCommand()
    {
        this.LastFailedCommand = null;
        this.LastFailureAt = null;

        if (this.LastStatus.StartsWith("Failed", StringComparison.OrdinalIgnoreCase))
            this.LastStatus = "Failure cleared. Native chat sender ready.";
    }

    /// <summary>
    /// Queues any native chat command and waits after it before the next queued command.
    /// </summary>
    public void QueueCommandWithDelay(string command, TimeSpan delayAfterCommand)
    {
        this.QueueCommand(command, delayAfterCommand);
    }

    /// <summary>
    /// Queues a /sh message and waits after it before the next queued command.
    /// </summary>
    public void QueueShoutWithDelay(string message, TimeSpan delayAfterCommand)
    {
        this.QueueChatMessage("/sh", message, delayAfterCommand);
    }

    private void QueueChatMessage(string channelCommand, string message)
    {
        this.QueueChatMessage(channelCommand, message, TimeSpan.Zero);
    }

    private void QueueChatMessage(string channelCommand, string message, TimeSpan delayAfterCommand)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            this.LastStatus = "Not queued: message was empty.";
            return;
        }

        var cleanMessage = CleanSingleLine(message);

        if (string.IsNullOrWhiteSpace(cleanMessage))
        {
            this.LastStatus = "Not queued: cleaned message was empty.";
            return;
        }

        this.QueueCommand($"{channelCommand} {cleanMessage}", delayAfterCommand);
    }

    private void QueueCommand(string command, TimeSpan delayAfterCommand)
    {
        this.QueueCommand(command, delayAfterCommand, isManual: false);
    }

    private void QueueCommand(string command, TimeSpan delayAfterCommand, bool isManual, string? staffRole = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            this.LastStatus = "Not queued: command was empty.";
            return;
        }

        if (this.DispatchPaused && !isManual)
        {
            this.LastStatus = "Automation paused: automatic command not queued.";
            return;
        }

        var cleanCommand = CleanSingleLine(command);

        // Plain lines are now intentional announcement content. Explicit commands
        // retain their original slash prefix and are never changed to Yell/Shout.

        this.pendingCommands.Enqueue(new QueuedChatCommand(cleanCommand, delayAfterCommand, isManual, staffRole));
        this.LastStatus = $"Queued command: {cleanCommand}";
    }

    /// <summary>
    /// Executes one queued command when allowed.
    /// Call this from IFramework.Update.
    /// </summary>
    public void FlushOnePendingCommand()
    {
        if (this.pendingCommands.Count == 0)
            return;

        // PAUSE ALL blocks only automatic queue items. Manual macros are still
        // executed one command at a time using the normal FFXIV pacing rules.
        if (this.DispatchPaused && !this.pendingCommands.Peek().IsManual)
            return;

        if (DateTime.Now < this.nextCommandAllowedAt)
            return;

        var queuedCommand = this.pendingCommands.Peek();
        var outgoing = queuedCommand.Command;
        if (!outgoing.StartsWith('/'))
        {
            // No timers: always use the opposite of the last observed Yell/Shout.
            // A fresh plugin session begins with Yell. When disabled, always Yell.
            var useShout = this.SmartChannelsEnabled && this.lastWasYell == true;
            outgoing = (useShout ? "/sh " : "/y ") + outgoing;
        }
        this.pendingCommands.Dequeue();
        var sent = this.TryExecuteCommandNow(outgoing);

        if (sent)
        {
            this.LastFailedCommand = null;
            this.LastFailureAt = null;
            this.LastStatus = $"Sent command: {outgoing}";
            // Record our own successful sends immediately; the chat event may arrive later.
            if (outgoing.StartsWith("/y ", StringComparison.OrdinalIgnoreCase)) this.lastWasYell = true;
            else if (outgoing.StartsWith("/sh ", StringComparison.OrdinalIgnoreCase)) this.lastWasYell = false;
            this.nextCommandAllowedAt = DateTime.Now.Add(queuedCommand.DelayAfterCommand);
        }
        else
        {
            var detail = this.LastStatus;
            this.LastFailedCommand = outgoing;
            this.LastFailureAt = DateTime.Now;
            this.LastStatus = string.IsNullOrWhiteSpace(detail)
                ? $"Failed to send command: {outgoing}"
                : $"Failed to send command: {outgoing} ({detail})";
            this.nextCommandAllowedAt = DateTime.Now.AddSeconds(1);
        }
    }

    /// <summary>
    /// Executes a native game command immediately.
    ///
    /// This method is unsafe because it calls game client structures directly.
    /// Keep calls funneled through the queue where possible.
    /// </summary>
    private unsafe bool TryExecuteCommandNow(string command)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                this.LastStatus = "Failed: command was empty.";
                return false;
            }

            if (!command.StartsWith('/'))
            {
                this.LastStatus = $"Failed: command must start with '/'. Got: {command}";
                return false;
            }

            var shellModule = RaptureShellModule.Instance();
            if (shellModule is null)
            {
                this.LastStatus = "Failed: RaptureShellModule.Instance() was null.";
                return false;
            }

            var uiModule = UIModule.Instance();
            if (uiModule is null)
            {
                this.LastStatus = "Failed: UIModule.Instance() was null.";
                return false;
            }

            using var commandString = new Utf8String(command);

            if (commandString.Length <= 0)
            {
                this.LastStatus = "Failed: encoded command was empty.";
                return false;
            }

            if (commandString.Length > 500)
            {
                this.LastStatus = $"Failed: command was too long. Length: {commandString.Length}.";
                return false;
            }

            shellModule->ExecuteCommandInner(&commandString, uiModule);

            return true;
        }
        catch (Exception ex)
        {
            this.LastStatus = $"Failed: {ex.GetType().Name}: {ex.Message}";
            this.log.Error(ex, "Failed to execute a native chatbox command.");
            return false;
        }
    }


    private static TimeSpan? TryParseWaitLine(string line)
    {
        var match = Regex.Match(line, @"^<wait\.(?<seconds>\d+(?:\.\d+)?)>$", RegexOptions.IgnoreCase);

        if (!match.Success)
            return null;

        if (!double.TryParse(match.Groups["seconds"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return null;

        if (seconds < 0)
            seconds = 0;

        if (seconds > 60)
            seconds = 60;

        return TimeSpan.FromSeconds(seconds);
    }

    private static string CleanSingleLine(string message)
    {
        return message
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    private sealed record QueuedChatCommand(string Command, TimeSpan DelayAfterCommand, bool IsManual, string? StaffRole);
}
