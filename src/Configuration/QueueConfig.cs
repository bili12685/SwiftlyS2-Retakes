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
  /// How players are chosen when the roster changes. Queue priority flags always sort
  /// ahead of non-priority players on the way in, unless noted.
  /// </summary>
  /// <remarks>
  /// <list type="bullet">
  /// <item><c>"fifo"</c> (default) — the longest-waiting player is promoted, and the most
  /// recently active player is the one moved to spectator.</item>
  /// <item><c>"random"</c> — promotion is shuffled; demotion is still most-recent-first.</item>
  /// <item><c>"allrandom"</c> — promotion and demotion are both shuffled. Queue-priority
  /// players are exempt from demotion and keep playing (unless there are too few others
  /// to make room).</item>
  /// </list>
  /// Anything else falls back to <c>"fifo"</c>.
  /// </remarks>
  public string PromotionOrder { get; set; } = "fifo";
}
