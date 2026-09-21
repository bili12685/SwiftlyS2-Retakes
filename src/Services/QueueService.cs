using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2_Retakes.Interfaces;
using SwiftlyS2_Retakes.Logging;
using SwiftlyS2_Retakes.Utils;

namespace SwiftlyS2_Retakes.Services;

public sealed class QueueService : IQueueService
{
  private readonly ISwiftlyCore _core;
  private readonly ILogger _logger;
  private readonly IRetakesConfigService _config;
  private readonly IMessageService _messages;
  private readonly IRetakesStateService _state;

  private readonly HashSet<ulong> _activePlayers = new();
  private readonly HashSet<ulong> _queuePlayers = new();
  private readonly HashSet<ulong> _roundTerrorists = new();
  private readonly HashSet<ulong> _roundCounterTerrorists = new();

  // Guards against scheduling TerminateRound more than once per round.
  // Calling TerminateRound twice within the same world-update tick (e.g. when
  // multiple disconnects/team-changes each invoke CheckRoundDone) crashes inside
  // the native game-rules code because the round is already in its end-delay state.
  // Reset on round start (SetRoundTeams) and round end (ClearRoundTeams).
  private bool _terminationScheduled;

  public IReadOnlySet<ulong> ActivePlayers => _activePlayers;
  public IReadOnlySet<ulong> QueuePlayers => _queuePlayers;

  public int ActiveCount => _activePlayers.Count;
  public int QueueCount => _queuePlayers.Count;

  public QueueService(ISwiftlyCore core, ILogger logger, IRetakesConfigService config, IMessageService messages, IRetakesStateService state)
  {
    _core = core;
    _logger = logger;
    _config = config;
    _messages = messages;
    _state = state;
  }

  public int GetTargetNumTerrorists()
  {
    var cfg = _config.Config.Queue;
    var shouldForceEven = cfg.ForceEvenTeamsWhenPlayerCountIsMultipleOf10 && _activePlayers.Count % 10 == 0;
    var ratio = (shouldForceEven ? 0.5f : _config.Config.TeamBalance.TerroristRatio) * _activePlayers.Count;
    var numTerrorists = (int)MathF.Round(ratio);
    return numTerrorists > 0 ? numTerrorists : 1;
  }

  public int GetTargetNumCounterTerrorists()
  {
    return _activePlayers.Count - GetTargetNumTerrorists();
  }

  public bool IsActive(ulong steamId) => _activePlayers.Contains(steamId);
  public bool IsQueued(ulong steamId) => _queuePlayers.Contains(steamId);

  public HookResult OnPlayerJoinedTeam(IPlayer player, Team fromTeam, Team toTeam)
  {
    if (!PlayerUtil.IsHuman(player))
    {
      return HookResult.Continue;
    }

    var steamId = player.SteamID;
    var cfg = _config.Config.Queue;

    _logger.LogPluginDebug("QueueService: [{Name}] Team change: {From} -> {To}", player.Controller.PlayerName, fromTeam, toTeam);

    // Allow initial connection to spectator
    if (fromTeam == Team.None && toTeam == Team.Spectator)
    {
      return HookResult.Continue;
    }

    // Player is already active
    if (_activePlayers.Contains(steamId))
    {
      _logger.LogPluginDebug("QueueService: [{Name}] Player is active", player.Controller.PlayerName);

      // Switching to spectator - remove from active
      if (toTeam == Team.Spectator)
      {
        _logger.LogPluginInformation("QueueService: [{Name}] Switched to spectator", player.Controller.PlayerName);
        RemovePlayerFromQueues(steamId);
        return HookResult.Continue;
      }

      // Check for mid-round team change prevention
      var rules = _core.EntitySystem.GetGameRules();
      if (!cfg.PreventTeamChangesMidRound || (rules is not null && rules.WarmupPeriod))
      {
        return HookResult.Continue;
      }

      // Prevent switching to a team they weren't on at round start
      if (_roundTerrorists.Count > 0 && _roundCounterTerrorists.Count > 0)
      {
        var tryingToJoinCT = toTeam == Team.CT && !_roundCounterTerrorists.Contains(steamId);
        var tryingToJoinT = toTeam == Team.T && !_roundTerrorists.Contains(steamId);

        if (tryingToJoinCT || tryingToJoinT)
        {
          _logger.LogPluginInformation("QueueService: [{Name}] Prevented mid-round team change", player.Controller.PlayerName);
          _activePlayers.Remove(steamId);
          _queuePlayers.Add(steamId);

          // Kill and move to spectator
          if (player.Controller.PawnIsAlive && player.Pawn is not null)
          {
            player.Pawn.CommitSuicide(false, true);
          }

          player.ChangeTeam(Team.Spectator);
          return HookResult.Handled;
        }
      }

      CheckRoundDone();
      return HookResult.Continue;
    }

    // Player is not active - check if we can add them
    if (!_queuePlayers.Contains(steamId))
    {
      var rules = _core.EntitySystem.GetGameRules();
      var isWarmup = rules is not null && rules.WarmupPeriod;

      // During warmup, add directly to active if there's room
      if (isWarmup && _activePlayers.Count < cfg.MaxPlayers)
      {
        _logger.LogPluginInformation("QueueService: [{Name}] Added to active players (warmup)", player.Controller.PlayerName);
        _activePlayers.Add(steamId);
        return HookResult.Continue;
      }

      // Add to queue
      _logger.LogPluginInformation("QueueService: [{Name}] Added to queue", player.Controller.PlayerName);
      var loc = _core.Translation.GetPlayerLocalizer(player);
      _messages.Chat(player, loc["queue.added"]);
      _queuePlayers.Add(steamId);

      if (!isWarmup && toTeam != Team.Spectator)
      {
        if (player.Controller.PawnIsAlive && player.Pawn is not null)
        {
          player.Pawn.CommitSuicide(false, true);
        }

        player.ChangeTeam(Team.Spectator);
        return HookResult.Stop;
      }
    }

    // Player is already queued and tries to join T/CT — block until next round
    if (toTeam == Team.T || toTeam == Team.CT)
    {
      CheckRoundDone();
      return HookResult.Stop;
    }

    // Allow queued player to spectate (removes from queue)
    if (toTeam == Team.Spectator || toTeam == Team.None)
    {
      RemovePlayerFromQueues(steamId);
      return HookResult.Continue;
    }

    return HookResult.Continue;
  }

  // Keeps the team-select menu from auto-closing once a connecting player has
  // been parked in spectator, so they can pick a side at their own pace.
  private const float TeamMenuHoldSeconds = 3600f;

  public void OnPlayerConnected(IPlayer player)
  {
    if (!PlayerUtil.IsHuman(player))
    {
      return;
    }

    var cfg = _config.Config.Queue;

    // AutoJoinGame wins: no menu, straight into the game or the queue.
    if (cfg.AutoJoinGame)
    {
      AddConnectedPlayerToGame(player, cfg);
      return;
    }

    if (cfg.AutoJoinSpectators)
    {
      MoveToSpectatorWithTeamMenu(player);
    }
  }

  private void MoveToSpectatorWithTeamMenu(IPlayer player)
  {
    var controller = player.Controller;
    if (controller is null)
    {
      return;
    }

    _logger.LogPluginDebug("QueueService: [{Name}] Auto-joining spectator on connect", controller.PlayerName);

    if ((Team)controller.TeamNum != Team.Spectator)
    {
      player.ChangeTeam(Team.Spectator);
    }

    // ForceTeamTime is a native-backed ref, so keep the write guarded.
    try
    {
      controller.ForceTeamTime.Value = _core.Engine.GlobalVars.CurrentTime + TeamMenuHoldSeconds;
      controller.ForceTeamTimeUpdated();
    }
    catch (Exception ex)
    {
      _logger.LogPluginDebug("QueueService: [{Name}] Could not extend ForceTeamTime: {Error}", controller.PlayerName, ex.Message);
    }
  }

  private void AddConnectedPlayerToGame(IPlayer player, Configuration.QueueConfig cfg)
  {
    var steamId = player.SteamID;
    if (_activePlayers.Contains(steamId) || _queuePlayers.Contains(steamId))
    {
      return;
    }

    var controller = player.Controller;
    if (controller is null)
    {
      return;
    }

    if (_activePlayers.Count < cfg.MaxPlayers)
    {
      _logger.LogPluginInformation("QueueService: [{Name}] Auto-joined the game on connect", controller.PlayerName);
      _activePlayers.Add(steamId);
      _queuePlayers.Remove(steamId);

      _state.BeginTeamChangeBypass();
      try { player.SwitchTeam(Team.CT); }
      finally { _state.EndTeamChangeBypass(); }
      return;
    }

    _logger.LogPluginInformation("QueueService: [{Name}] Auto-joined the queue on connect (server full)", controller.PlayerName);
    _queuePlayers.Add(steamId);

    if (controller.PawnIsAlive && player.Pawn is not null)
    {
      player.Pawn.CommitSuicide(false, true);
    }

    player.ChangeTeam(Team.Spectator);

    var loc = _core.Translation.GetPlayerLocalizer(player);
    _messages.Chat(player, loc["queue.added"]);
  }

  public void Update()
  {
    RemoveDisconnectedPlayers();

    var cfg = _config.Config.Queue;
    _logger.LogDebug("QueueService: Update: Max={Max}, Active={Active}, Queue={Queue}",
      cfg.MaxPlayers, _activePlayers.Count, _queuePlayers.Count);

    // Hard-enforce MaxPlayers: any human on T/CT beyond the limit gets moved to spectator.
    // This catches players who bypassed queue tracking (engine auto-assign, warmup overflow, etc.)
    EnforceMaxPlayers(cfg);

    var playersToAdd = cfg.MaxPlayers - _activePlayers.Count;
    if (playersToAdd > 0 && _queuePlayers.Count > 0)
    {
      var allPlayers = _core.PlayerManager.GetAllPlayers().Where(p => p.IsValid).ToList();

      // Prioritize players with queue priority, then by slot (join order)
      var playersToAddList = _queuePlayers
        .Select(steamId => allPlayers.FirstOrDefault(p => p.SteamID == steamId))
        .Where(p => p is not null && p.IsValid)
        .OrderBy(p => HasQueuePriority(p!) ? 0 : 1)
        .ThenBy(p => p!.Slot)
        .Take(playersToAdd)
        .ToList();

      foreach (var player in playersToAddList)
      {
        if (player is null || !player.IsValid) continue;

        _queuePlayers.Remove(player.SteamID);
        _activePlayers.Add(player.SteamID);
        _state.BeginTeamChangeBypass();
        try { player.SwitchTeam(Team.CT); }
        finally { _state.EndTeamChangeBypass(); }
        _logger.LogInformation("QueueService: Moved {Name} from queue to active", player.Controller.PlayerName);
      }
    }

    HandleQueuePriority();

    // Notify queued players
    if (_activePlayers.Count >= cfg.MaxPlayers && _queuePlayers.Count > 0)
    {
      var allPlayers = _core.PlayerManager.GetAllPlayers().Where(p => p.IsValid).ToList();
      foreach (var steamId in _queuePlayers)
      {
        var player = allPlayers.FirstOrDefault(p => p.SteamID == steamId);
        if (player is null || !player.IsValid) continue;

        var loc = _core.Translation.GetPlayerLocalizer(player);
        _messages.Chat(player, loc["queue.waiting", _activePlayers.Count, cfg.MaxPlayers]);
      }
    }
  }

  private void EnforceMaxPlayers(Configuration.QueueConfig cfg)
  {
    var allPlayers = _core.PlayerManager.GetAllPlayers().Where(p => p.IsValid).ToList();

    // Get all humans currently on T or CT
    var teamPlayers = allPlayers
      .Where(PlayerUtil.IsHuman)
      .Where(p => (Team)p.Controller.TeamNum == Team.T || (Team)p.Controller.TeamNum == Team.CT)
      .ToList();

    // Sync _activePlayers with reality: add any untracked team players, remove stale entries
    foreach (var p in teamPlayers)
    {
      _activePlayers.Add(p.SteamID);
      _queuePlayers.Remove(p.SteamID);
    }

    if (teamPlayers.Count <= cfg.MaxPlayers)
      return;

    var maxPerTeam = cfg.MaxPlayers / 2;
    var excessCount = teamPlayers.Count - cfg.MaxPlayers;

    // Pick excess players to remove: newest first (highest slot), non-VIP first
    var toRemove = teamPlayers
      .OrderBy(p => HasQueuePriority(p) ? 1 : 0)
      .ThenByDescending(p => p.Slot)
      .Take(excessCount)
      .ToList();

    _state.BeginTeamChangeBypass();
    try
    {
      foreach (var player in toRemove)
      {
        _activePlayers.Remove(player.SteamID);
        _queuePlayers.Add(player.SteamID);

        if (player.Controller.PawnIsAlive && player.Pawn is not null)
        {
          player.Pawn.CommitSuicide(false, true);
        }

        player.ChangeTeam(Team.Spectator);

        var loc = _core.Translation.GetPlayerLocalizer(player);
        _messages.Chat(player, loc["queue.added"]);
        _logger.LogInformation("QueueService: [{Name}] Moved to spectator (MaxPlayers={Max} exceeded, {Total} on teams)",
          player.Controller.PlayerName, cfg.MaxPlayers, teamPlayers.Count);
      }
    }
    finally
    {
      _state.EndTeamChangeBypass();
    }
  }

  private void HandleQueuePriority()
  {
    var cfg = _config.Config.Queue;
    if (_activePlayers.Count != cfg.MaxPlayers) return;

    var allPlayers = _core.PlayerManager.GetAllPlayers().Where(p => p.IsValid).ToList();

    var vipQueuePlayers = _queuePlayers
      .Select(steamId => allPlayers.FirstOrDefault(p => p.SteamID == steamId))
      .Where(p => p is not null && p.IsValid && HasQueuePriority(p!))
      .ToList();

    if (vipQueuePlayers.Count == 0) return;

    foreach (var vipPlayer in vipQueuePlayers)
    {
      if (vipPlayer is null || !vipPlayer.IsValid) continue;

      // Find replaceable non-VIP players (newest first by slot)
      var replaceablePlayers = _activePlayers
        .Select(steamId => allPlayers.FirstOrDefault(p => p.SteamID == steamId))
        .Where(p => p is not null && p.IsValid && !HasQueuePriority(p!) && !HasQueueImmunity(p!))
        .OrderByDescending(p => p!.Slot)
        .ToList();

      if (replaceablePlayers.Count == 0)
      {
        _logger.LogDebug("QueueService: No replaceable players found");
        break;
      }

      var replaceablePlayer = replaceablePlayers.First()!;

      // Swap the players
      if (replaceablePlayer.Controller.PawnIsAlive && replaceablePlayer.Pawn is not null)
      {
        replaceablePlayer.Pawn.CommitSuicide(false, true);
      }
      replaceablePlayer.ChangeTeam(Team.Spectator);
      _activePlayers.Remove(replaceablePlayer.SteamID);
      _queuePlayers.Add(replaceablePlayer.SteamID);
      var replaceableLoc = _core.Translation.GetPlayerLocalizer(replaceablePlayer);
      _messages.Chat(replaceablePlayer, replaceableLoc["queue.moved_out", vipPlayer.Controller.PlayerName]);

      _activePlayers.Add(vipPlayer.SteamID);
      _queuePlayers.Remove(vipPlayer.SteamID);
      _state.BeginTeamChangeBypass();
      try { vipPlayer.SwitchTeam(Team.CT); }
      finally { _state.EndTeamChangeBypass(); }
      var vipLoc = _core.Translation.GetPlayerLocalizer(vipPlayer);
      _messages.Chat(vipPlayer, vipLoc["queue.moved_in", replaceablePlayer.Controller.PlayerName]);

      _logger.LogInformation("QueueService: VIP {Vip} replaced {Replaced}", vipPlayer.Controller.PlayerName, replaceablePlayer.Controller.PlayerName);
    }
  }

  private void RemoveDisconnectedPlayers()
  {
    var allPlayers = _core.PlayerManager.GetAllPlayers().Where(p => p.IsValid).ToList();
    var connectedSteamIds = allPlayers.Select(p => p.SteamID).ToHashSet();

    var disconnectedActive = _activePlayers.Where(id => !connectedSteamIds.Contains(id)).ToList();
    if (disconnectedActive.Count > 0)
    {
      _logger.LogDebug("QueueService: Removing {Count} disconnected active players", disconnectedActive.Count);
      foreach (var id in disconnectedActive)
      {
        _activePlayers.Remove(id);
        _roundTerrorists.Remove(id);
        _roundCounterTerrorists.Remove(id);
      }
    }

    var disconnectedQueue = _queuePlayers.Where(id => !connectedSteamIds.Contains(id)).ToList();
    if (disconnectedQueue.Count > 0)
    {
      _logger.LogDebug("QueueService: Removing {Count} disconnected queue players", disconnectedQueue.Count);
      foreach (var id in disconnectedQueue)
      {
        _queuePlayers.Remove(id);
      }
    }
  }

  public void RemovePlayerFromQueues(ulong steamId)
  {
    _activePlayers.Remove(steamId);
    _queuePlayers.Remove(steamId);
    _roundTerrorists.Remove(steamId);
    _roundCounterTerrorists.Remove(steamId);
    _logger.LogDebug("QueueService: Removed {SteamId} from all queues", steamId);
    CheckRoundDone();
  }

  // Returns true if the game-rules state is unsafe to call TerminateRound against
  // (freeze period, warmup, game restart pending, or a round-win is already recorded).
  // Calling native TerminateRound while the engine is in any of these transitional
  // states has been observed to crash inside CCSGameRules::TerminateRound on Linux.
  private static bool IsUnsafeForTermination(SwiftlyS2.Shared.SchemaDefinitions.CCSGameRules rules)
  {
    if (rules.WarmupPeriod) return true;
    if (rules.FreezePeriod) return true;
    if (rules.GameRestart) return true;
    // RoundWinStatus != 0 means the engine has already recorded a winner for this
    // round and a round-end sequence is in progress; another TerminateRound here
    // would re-enter the round-end path and can crash.
    if (rules.RoundWinStatus != 0) return true;
    return false;
  }

  public void CheckRoundDone()
  {
    var rules = _core.EntitySystem.GetGameRules();
    if (rules is null) return;
    if (IsUnsafeForTermination(rules)) return;

    // If the round is already ending (e.g. bomb defused, EventRoundEnd already fired),
    // do not call TerminateRound again — it would override the in-progress round-end
    // delay and cause the next round to start almost instantly.
    if (!_state.RoundLive)
    {
      _terminationScheduled = false;
      return;
    }

    // Already scheduled a TerminateRound this round — bail out to prevent a
    // second native call before EventRoundEnd has had a chance to flip RoundLive.
    if (_terminationScheduled) return;

    var allPlayers = _core.PlayerManager.GetAllPlayers().Where(p => p.IsValid).ToList();

    var tCount = allPlayers.Count(p => (Team)p.Controller.TeamNum == Team.T && p.Controller.PawnIsAlive);
    var ctCount = allPlayers.Count(p => (Team)p.Controller.TeamNum == Team.CT && p.Controller.PawnIsAlive);

    if (tCount != 0 && ctCount != 0) return;

    _logger.LogInformation("QueueService: CheckRoundDone - T:{T} CT:{CT}, scheduling round termination", tCount, ctCount);

    _terminationScheduled = true;

    // Use the server's configured round-restart delay so the scoreboard is shown
    // for the expected duration (mp_round_restart_delay, defaulting to 2s).
    var restartDelay = _core.ConVar.CreateOrFind("mp_round_restart_delay", "", 2.0f)?.Value ?? 2.0f;

    // Defer TerminateRound off the current event dispatch. Calling it synchronously
    // from inside a game event callback (e.g. EventPlayerTeam / EventClientDisconnect)
    // can crash the native game-rules code due to re-entrancy while the engine is
    // still dispatching the originating event. A short delay (rather than just the
    // next world update) also lets the engine settle out of any transitional
    // freeze/round-start state that has been observed to crash TerminateRound when
    // the round has only just been reset.
    _core.Scheduler.DelayBySeconds(0.25f, () =>
    {
      // Re-validate state on the main thread — round may have already ended, restarted,
      // or entered warmup between scheduling and execution.
      var currentRules = _core.EntitySystem.GetGameRules();
      if (currentRules is null) return;
      if (IsUnsafeForTermination(currentRules)) return;
      if (!_state.RoundLive) return;

      var players = _core.PlayerManager.GetAllPlayers().Where(p => p.IsValid).ToList();
      var tAlive = players.Count(p => (Team)p.Controller.TeamNum == Team.T && p.Controller.PawnIsAlive);
      var ctAlive = players.Count(p => (Team)p.Controller.TeamNum == Team.CT && p.Controller.PawnIsAlive);
      if (tAlive != 0 && ctAlive != 0) return;

      var currentReason = ctAlive == 0 ? RoundEndReason.TerroristsWin : RoundEndReason.CTsWin;

      try
      {
        currentRules.TerminateRound(currentReason, restartDelay);
      }
      catch (Exception ex)
      {
        _logger.LogWarning("QueueService: Failed to terminate round: {Error}", ex.Message);

        // Fallback: kill all remaining players to force round end
        foreach (var player in players.Where(p => p.Controller.PawnIsAlive && p.Pawn is not null))
        {
          player.Pawn!.CommitSuicide(false, true);
        }
      }
    });
  }

  public void SetRoundTeams()
  {
    // Always clear the termination guard at round start, regardless of the
    // mid-round team-change setting below.
    _terminationScheduled = false;

    var cfg = _config.Config.Queue;
    if (!cfg.PreventTeamChangesMidRound) return;

    _roundTerrorists.Clear();
    _roundCounterTerrorists.Clear();

    var allPlayers = _core.PlayerManager.GetAllPlayers().Where(p => p.IsValid).ToList();

    foreach (var steamId in _activePlayers)
    {
      var player = allPlayers.FirstOrDefault(p => p.SteamID == steamId);
      if (player is null || !player.IsValid) continue;

      var team = (Team)player.Controller.TeamNum;
      if (team == Team.T)
      {
        _roundTerrorists.Add(steamId);
      }
      else if (team == Team.CT)
      {
        _roundCounterTerrorists.Add(steamId);
      }
    }

    _logger.LogDebug("QueueService: Round teams set: {T} T, {CT} CT", _roundTerrorists.Count, _roundCounterTerrorists.Count);
  }

  public void ClearRoundTeams()
  {
    _roundTerrorists.Clear();
    _roundCounterTerrorists.Clear();
    _terminationScheduled = false;
    _logger.LogDebug("QueueService: Round teams cleared");
  }

  public void Reset()
  {
    _activePlayers.Clear();
    _queuePlayers.Clear();
    _roundTerrorists.Clear();
    _roundCounterTerrorists.Clear();
    _terminationScheduled = false;
    _logger.LogDebug("QueueService: Reset all queues");
  }

  private bool HasQueuePriority(IPlayer player)
  {
    var flags = _config.Config.Queue.QueuePriorityFlags;
    if (string.IsNullOrWhiteSpace(flags)) return false;

    var flagList = flags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var flag in flagList)
    {
      if (_core.Permission.PlayerHasPermission(player.SteamID, flag))
      {
        return true;
      }
    }

    return false;
  }

  private bool HasQueueImmunity(IPlayer player)
  {
    var flags = _config.Config.Queue.QueueImmunityFlags;
    if (string.IsNullOrWhiteSpace(flags))
    {
      // Fall back to priority flags if immunity not specified
      return HasQueuePriority(player);
    }

    var flagList = flags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var flag in flagList)
    {
      if (_core.Permission.PlayerHasPermission(player.SteamID, flag))
      {
        return true;
      }
    }

    return false;
  }

  public string DebugSummary()
  {
    var allPlayers = _core.PlayerManager.GetAllPlayers().Where(p => p.IsValid).ToList();

    var activeNames = _activePlayers
      .Select(id => allPlayers.FirstOrDefault(p => p.SteamID == id)?.Controller?.PlayerName ?? id.ToString())
      .ToList();

    var queueNames = _queuePlayers
      .Select(id => allPlayers.FirstOrDefault(p => p.SteamID == id)?.Controller?.PlayerName ?? id.ToString())
      .ToList();

    return $"Active ({_activePlayers.Count}): [{string.Join(", ", activeNames)}] | Queue ({_queuePlayers.Count}): [{string.Join(", ", queueNames)}]";
  }
}
