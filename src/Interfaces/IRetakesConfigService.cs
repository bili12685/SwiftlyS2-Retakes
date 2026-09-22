using SwiftlyS2_Retakes.Configuration;

namespace SwiftlyS2_Retakes.Interfaces;

/// <summary>
/// Service for managing retakes plugin configuration.
/// </summary>
public interface IRetakesConfigService
{
  /// <summary>
  /// Gets the path to the configuration file.
  /// </summary>
  string ConfigPath { get; }

  /// <summary>
  /// Gets the current configuration.
  /// </summary>
  RetakesConfig Config { get; }

  /// <summary>
  /// Schema version the config file declared, as written -- read before migration.
  /// A config with no version field reports the pre-versioning version (1), because
  /// that is what it is.
  /// </summary>
  int DeclaredConfigVersion { get; }

  /// <summary>
  /// False when the config is still older than this build supports *after* migration.
  /// A pre-versioning config is upgraded and stamped first, so it passes; only an
  /// explicitly declared old version fails. The caller should refuse to start rather
  /// than run against a shape this build does not fully understand.
  /// </summary>
  bool IsConfigVersionSupported { get; }

  /// <summary>
  /// Loads or creates the configuration file.
  /// </summary>
  void LoadOrCreate();

  /// <summary>
  /// Saves the current configuration to disk.
  /// </summary>
  void Save();

  /// <summary>
  /// Applies configuration values to convars.
  /// </summary>
  /// <param name="restartGame">Whether to restart the game after applying</param>
  void ApplyToConvars(bool restartGame = false);
}
