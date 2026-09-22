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
  private readonly Random _random;

  // Membership maps to a monotonically increasing sequence number recording when the
  // player entered that set, so ordering reflects how long they have actually been
  // waiting rather than an unrelated engine property. HashSet could not represent
  // arrival order, which is why promotion used to fall back to Player.Slot -- the
  // client's connection index, stable for the life of a connection, so the same
  // players won every time.
  private readonly Dictionary<ulong, long> _activePlayers = new();
  private readonly Dictionary<ulong, long> _queuePlayers = new();
  private readonly HashSet<ulong> _roundTerrorists = new();
  private readonly HashSet<ulong> _roundCounterTerrorists = new();
  private long _nextSequence;

  // Guards against scheduling TerminateRound more than once per round.
  // Calling TerminateRound twice within the same world-update tick (e.g. when
  // multiple disconnects/team-changes each invoke CheckRoundDone) crashes inside
  // the native game-rules code because the round is already in its end-delay state.
  // Reset on round start (SetRoundTeams) and round end (ClearRoundTeams).
  private bool _terminationScheduled;

  public IReadOnlyCollection<ulong> ActivePlayers => _activePlayers.Keys;
  public IReadOnlyCollection<ulong> QueuePlayers => _queuePlayers.Keys;

  public int ActiveCount => _activePlayers.Count;
  public int QueueCount => _queuePlayers.Count;

  public QueueService(ISwiftlyCore core, ILogger logger, IRetakesConfigService config, IMessageService messages, IRetakesStateService state, Random random)
  {
    _core = core;
    _logger = logger;
    _config = config;
    _messages = messages;
    _state = state;
    _random = random;
  }

  /// <summary>
  /// Adds a player to the waiting queue, stamping their arrival position. A player
  /// who is already waiting keeps the position they had, so re-queueing them does
  /// not push them to the back of the line.
  /// </summary>
  private void EnqueuePlayer(ulong steamId)
  {
    if (!_queuePlayers.ContainsKey(steamId))
    {
      _queuePlayers[steamId] = _nextSequence++;
    }
  }

  /// <summary>
  /// Moves a player into the active set and stamps when they got there. Idempotent:
  /// a player who is already active keeps their original stamp, so the per-round
  /// re-adoption in <see cref="EnforceMaxPlayers"/> cannot reshuffle the order.
  /// </summary>
  private void MarkActive(ulong steamId)
  {
    _queuePlayers.Remove(steamId);
    if (!_activePlayers.ContainsKey(steamId))
    {
      _activePlayers[steamId] = _nextSequence++;
    }
  }

  /// <summary>
  /// How <c>Queue.PromotionOrder</c> was configured.
  /// </summary>
  private enum PromotionMode
  {
    /// <summary>Longest-waiting first, in both directions.</summary>
    Fifo,

    /// <summary>Promotion is shuffled; demotion still removes the most recent arrival.</summary>
    Random,

    /// <summary>Promotion and demotion are both shuffled. Queue priority is exempt from demotion.</summary>
    AllRandom,
  }

  /// <summary>
  /// Reads <c>Queue.PromotionOrder</c>. Anything unrecognised falls back to FIFO, so a
  /// typo cannot silently turn the queue into a lottery.
  /// </summary>
  private PromotionMode CurrentPromotionMode()
  {
    var value = (_config.Config.Queue.PromotionOrder ?? string.Empty).Trim();
    if (value.Equals("allrandom", StringComparison.OrdinalIgnoreCase)) return PromotionMode.AllRandom;
    if (value.Equals("random", StringComparison.OrdinalIgnoreCase)) return PromotionMode.Random;
    return PromotionMode.Fifo;
  }

  /// <summary>
  /// Orders waiting players for promotion: queue priority first, then either
  /// longest-waiting or a shuffle depending on the configured mode.
  /// </summary>
  private List<IPlayer> OrderQueueForPromotion(List<IPlayer> allPlayers)
  {
    var eligible = _queuePlayers
      .Select(entry => (Player: allPlayers.FirstOrDefault(p => p.SteamID == entry.Key), Entry: entry))
      .Where(x => x.Player is not null && x.Player.IsValid)
      .OrderBy(x => HasQueuePriority(x.Player!) ? 0 : 1);

    var ordered = CurrentPromotionMode() == PromotionMode.Fifo
      ? eligible.ThenBy(x => x.Entry.Value)
      : eligible.ThenBy(_ => _random.Next());

    return ordered.Select(x => x.Player!).ToList();
  }

  /// <summary>
  /// Chooses who is pushed out to spectator when the server holds more players than
  /// <c>Queue.MaxPlayers</c> allows.
  /// </summary>
  /// <remarks>
  /// FIFO and Random both take the most recently active first, considering queue-priority
  /// players last. AllRandom shuffles instead and treats queue priority as an exemption,
  /// so a priority player keeps playing rather than being picked by chance.
  ///
  /// The exemption yields only when there are not enough other players to make room:
  /// without that fallback the cap could not be enforced at all and the server would sit
  /// above MaxPlayers indefinitely.
  /// </remarks>
  private List<IPlayer> SelectForDemotion(List<IPlayer> teamPlayers, int excessCount)
  {
    if (CurrentPromotionMode() == PromotionMode.AllRandom)
    {
      var exempt = teamPlayers.Where(HasQueuePriority).ToList();
      var candidates = teamPlayers.Where(p => !HasQueuePriority(p)).ToList();

      var picked = candidates.OrderBy(_ => _random.Next()).Take(excessCount).ToList();

      var shortfall = excessCount - picked.Count;
      if (shortfall > 0)
      {
        _logger.LogDebug("QueueService: only {Available} non-priority players for {Needed} slots, demoting {Extra} prioritised player(s)", candidates.Count, excessCount, shortfall);
        picked.AddRange(exempt.OrderBy(_ => _random.Next()).Take(shortfall));
      }

      return picked;
    }

    // Most recently active first, non-priority first.
    return teamPlayers
      .OrderBy(p => HasQueuePriority(p) ? 1 : 0)
      .ThenByDescending(p => _activePlayers.TryGetValue(p.SteamID, out var seq) ? seq : long.MinValue)
      .Take(excessCount)
      .ToList();
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

  public bool IsActive(ulong steamId) => _activePlayers.ContainsKey(steamId);
  public bool IsQueued(ulong steamId) => _queuePlayers.ContainsKey(steamId);

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
    if (_activePlayers.ContainsKey(steamId))
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
          EnqueuePlayer(steamId);

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
    if (!_queuePlayers.ContainsKey(steamId))
    {
      var rules = _core.EntitySystem.GetGameRules();
      var isWarmup = rules is not null && rules.WarmupPeriod;

      // During warmup, add directly to active if there's room
      if (isWarmup && _activePlayers.Count < cfg.MaxPlayers)
      {
        _logger.LogPluginInformation("QueueService: [{Name}] Added to active players (warmup)", player.Controller.PlayerName);
        MarkActive(steamId);
        return HookResult.Continue;
      }

      // Add to queue
      _logger.LogPluginInformation("QueueService: [{Name}] Added to queue", player.Controller.PlayerName);
      var loc = _core.Translation.GetPlayerLocalizer(player);
      _messages.Chat(player, loc["queue.added"]);
      EnqueuePlayer(steamId);

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

    // Moving the player to spectator is only half of it: parked there, the engine does
    // not offer the team menu on its own, so without this the player has no way to pick a
    // side at all. The client command is what opens the menu, and the upstream plugin
    // sends the same one. It is the engine's command, not the framework's, which is why
    // it does not appear anywhere in the SwiftlyS2 API.
    try
    {
      player.ExecuteCommand("teammenu");
    }
    catch (Exception ex)
    {
      _logger.LogPluginWarning(ex, "QueueService: failed to open the team menu for {Name}; they can still pick a side with the team keys", controller.PlayerName);
    }
  }

  private void AddConnectedPlayerToGame(IPlayer player, Configuration.QueueConfig cfg)
  {
    var steamId = player.SteamID;
    if (_activePlayers.ContainsKey(steamId) || _queuePlayers.ContainsKey(steamId))
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
      MarkActive(steamId);

      _state.BeginTeamChangeBypass();
      try { player.SwitchTeam(Team.CT); }
      finally { _state.EndTeamChangeBypass(); }
      return;
    }

    _logger.LogPluginInformation("QueueService: [{Name}] Auto-joined the queue on connect (server full)", controller.PlayerName);
    EnqueuePlayer(steamId);

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

      var playersToAddList = OrderQueueForPromotion(allPlayers).Take(playersToAdd).ToList();

      foreach (var player in playersToAddList)
      {
        if (player is null || !player.IsValid) continue;

        MarkActive(player.SteamID);
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
      foreach (var steamId in _queuePlayers.Keys)
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

    var teamSteamIds = teamPlayers.Select(p => p.SteamID).ToHashSet();

    // Anyone tracked as active who is no longer on a team has left the round --
    // whether they chose spectator or a safety mechanism such as the AFK manager put
    // them there. Drop them, because leaving them tracked held a phantom seat: it
    // inflated ActiveCount, which understated the free slots and stopped waiting
    // players from being promoted.
    //
    // They are deliberately NOT added to the waiting queue. Spectating is not a place
    // in line -- a player only enters the queue by picking a side again. (The waiting
    // queue legitimately contains spectators, so it is not pruned here; players who
    // rejoin a team were already pulled out of it by MarkActive.)
    var departed = _activePlayers.Keys.Where(id => !teamSteamIds.Contains(id)).ToList();
    foreach (var steamId in departed)
    {
      _activePlayers.Remove(steamId);
      _logger.LogDebug("QueueService: {SteamId} is no longer on a team, dropped from active", steamId);
    }

    // Sync _activePlayers with reality: adopt any team players the plugin has not seen
    foreach (var p in teamPlayers)
    {
      MarkActive(p.SteamID);
    }

    if (teamPlayers.Count <= cfg.MaxPlayers)
      return;

    var maxPerTeam = cfg.MaxPlayers / 2;
    var excessCount = teamPlayers.Count - cfg.MaxPlayers;

    var toRemove = SelectForDemotion(teamPlayers, excessCount);

    _state.BeginTeamChangeBypass();
    try
    {
      foreach (var player in toRemove)
      {
        _activePlayers.Remove(player.SteamID);
        EnqueuePlayer(player.SteamID);

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

    var vipQueuePlayers = _queuePlayers.Keys
      .Select(steamId => allPlayers.FirstOrDefault(p => p.SteamID == steamId))
      .Where(p => p is not null && p.IsValid && HasQueuePriority(p!))
      .ToList();

    if (vipQueuePlayers.Count == 0) return;

    foreach (var vipPlayer in vipQueuePlayers)
    {
      if (vipPlayer is null || !vipPlayer.IsValid) continue;

      // Find replaceable non-VIP players: most recently active first, by arrival
      // sequence rather than Player.Slot so the same player is not always the victim.
      var replaceablePlayers = _activePlayers.Keys
        .Select(steamId => allPlayers.FirstOrDefault(p => p.SteamID == steamId))
        .Where(p => p is not null && p.IsValid && !HasQueuePriority(p!) && !HasQueueImmunity(p!))
        .OrderByDescending(p => _activePlayers.TryGetValue(p!.SteamID, out var seq) ? seq : long.MinValue)
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
      EnqueuePlayer(replaceablePlayer.SteamID);
      var replaceableLoc = _core.Translation.GetPlayerLocalizer(replaceablePlayer);
      _messages.Chat(replaceablePlayer, replaceableLoc["queue.moved_out", vipPlayer.Controller.PlayerName]);

      MarkActive(vipPlayer.SteamID);
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

    var disconnectedActive = _activePlayers.Keys.Where(id => !connectedSteamIds.Contains(id)).ToList();
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

    var disconnectedQueue = _queuePlayers.Keys.Where(id => !connectedSteamIds.Contains(id)).ToList();
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

    foreach (var steamId in _activePlayers.Keys)
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

  /// <summary>
  /// Called on map load: clear everything and recount from zero. A map change forces
  /// every player to pick a side again, so both the active set and the waiting queue
  /// are rebuilt from whoever joins T/CT first on the new map. Anyone who lets the
  /// team-select timer run out drops to spectator and re-enters the queue when they
  /// pick a side.
  /// </summary>
  public void Reset()
  {
    _activePlayers.Clear();
    _queuePlayers.Clear();
    _roundTerrorists.Clear();
    _roundCounterTerrorists.Clear();
    _nextSequence = 0;
    _terminationScheduled = false;
    _logger.LogDebug("QueueService: Reset all queues, sequence restarted");
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

    var activeNames = _activePlayers.Keys
      .Select(id => allPlayers.FirstOrDefault(p => p.SteamID == id)?.Controller?.PlayerName ?? id.ToString())
      .ToList();

    var queueNames = _queuePlayers.Keys
      .Select(id => allPlayers.FirstOrDefault(p => p.SteamID == id)?.Controller?.PlayerName ?? id.ToString())
      .ToList();

    return $"Active ({_activePlayers.Count}): [{string.Join(", ", activeNames)}] | Queue ({_queuePlayers.Count}): [{string.Join(", ", queueNames)}]";
  }
}
