using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

namespace SwiftlyS2_Retakes.Interfaces;

/// <summary>
/// Service for managing player queue.
/// </summary>
public interface IQueueService
{
  IReadOnlyCollection<ulong> ActivePlayers { get; }
  IReadOnlyCollection<ulong> QueuePlayers { get; }
  int ActiveCount { get; }
  int QueueCount { get; }

  int GetTargetNumTerrorists();
  int GetTargetNumCounterTerrorists();

  bool IsActive(ulong steamId);
  bool IsQueued(ulong steamId);

  HookResult OnPlayerJoinedTeam(IPlayer player, Team fromTeam, Team toTeam);

  /// <summary>
  /// Handles a player who has just finished connecting: optionally parks them in
  /// spectator, or drops them straight into the game/queue. Configured by
  /// <c>Queue.AutoJoinSpectators</c> / <c>Queue.AutoJoinGame</c>; both off by default.
  /// </summary>
  void OnPlayerConnected(IPlayer player);

  void Update();
  void RemovePlayerFromQueues(ulong steamId);
  void CheckRoundDone();
  void SetRoundTeams();
  void ClearRoundTeams();
  void Reset();

  string DebugSummary();
}
