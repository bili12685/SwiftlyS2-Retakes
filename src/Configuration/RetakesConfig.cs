namespace SwiftlyS2_Retakes.Configuration;

/// <summary>
/// Root configuration for the Retakes plugin.
/// </summary>
public sealed class RetakesConfig
{
  /// <summary>
  /// Schema version of this build. Written as the first field of the config section so
  /// it is visible at the top of the file. A config without the field predates
  /// versioning; it is migrated and stamped on load rather than rejected.
  /// </summary>
  /// <remarks>
  /// Bump this whenever a change cannot be migrated in place. Old configs are refused
  /// once <see cref="MinimumSupportedVersion"/> moves past them.
  /// </remarks>
  public int ConfigVersion { get; set; } = CurrentVersion;

  /// <summary>The schema version this build writes and expects.</summary>
  public const int CurrentVersion = 2;

  /// <summary>
  /// Oldest schema version this build will run against. A config declaring less than
  /// this is refused and the plugin unloads itself, rather than starting with a shape
  /// it does not fully understand.
  /// </summary>
  /// <remarks>
  /// This matches <see cref="CurrentVersion"/>, so the effective rule is "the config
  /// must be current". A config with no version field predates versioning: it is
  /// upgraded in place and stamped with <see cref="CurrentVersion"/> before this check
  /// runs, so it complies rather than being refused -- reading it as version 0 would
  /// brick every existing server on upgrade. Only a config that explicitly declares an
  /// older version is turned away.
  /// </remarks>
  public const int MinimumSupportedVersion = 2;

  public AllocationConfig Allocation { get; set; } = new();
  public GrenadeConfig Grenades { get; set; } = new();
  public PreferencesConfig Preferences { get; set; } = new();
  public WeaponsConfig Weapons { get; set; } = new();
  public BombConfig Bomb { get; set; } = new();
  public SmokeScenarioConfig SmokeScenarios { get; set; } = new();
  public TeamBalanceConfig TeamBalance { get; set; } = new();
  public InstantBombConfig InstantBomb { get; set; } = new();
  public AntiTeamFlashConfig AntiTeamFlash { get; set; } = new();
  public AnnouncementConfig Announcement { get; set; } = new();
  public SoloBotConfig SoloBot { get; set; } = new();
  public AfkManagerConfig AfkManager { get; set; } = new();
  public ServerConfig Server { get; set; } = new();
  public BreakerConfig Breaker { get; set; } = new();
  public QueueConfig Queue { get; set; } = new();
  public DamageReportConfig DamageReport { get; set; } = new();
}
