namespace SwiftlyS2_Retakes.Configuration;

/// <summary>
/// Configuration for player queue.
/// </summary>
public sealed class QueueConfig
{
  public bool Enabled { get; set; } = true;
  public int MaxPlayers { get; set; } = 9;
  public bool PreventTeamChangesMidRound { get; set; } = true;
  public bool ForceEvenTeamsWhenPlayerCountIsMultipleOf10 { get; set; } = true;
  public string QueuePriorityFlags { get; set; } = "permission:vip";
  public string QueueImmunityFlags { get; set; } = "";
  public bool ShouldRemoveSpectators { get; set; } = true;

  /// <summary>
  /// Order in which waiting players are promoted when a slot opens.
  /// "fifo" (default) promotes the player who has been waiting longest;
  /// "random" shuffles the eligible players instead. Queue priority flags are
  /// applied first in both modes.
  /// </summary>
  public string PromotionOrder { get; set; } = "fifo";

  /// <summary>
  /// Park players in spectator when they connect so they pick their own side,
  /// keeping the team-select menu from auto-closing. Off by default.
  /// </summary>
  public bool AutoJoinSpectators { get; set; } = false;

  /// <summary>
  /// Drop players straight into the game on connect — or into the queue when the
  /// server is full — without showing the team-select menu. Takes priority over
  /// <see cref="AutoJoinSpectators"/>. Off by default.
  /// </summary>
  public bool AutoJoinGame { get; set; } = false;
}
