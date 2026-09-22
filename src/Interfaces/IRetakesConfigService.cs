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
  /// Schema version the config file declared. A config with no version field reports
  /// the pre-versioning version (1), because that is what it is.
  /// </summary>
  int DeclaredConfigVersion { get; }

  /// <summary>
  /// True when the config carries a version field that is not a number.
  /// </summary>
  bool IsConfigVersionMalformed { get; }

  /// <summary>
  /// False unless the config declares a numeric version at least as new as this build
  /// supports. A missing, out-of-date, or malformed declaration all fail. The caller
  /// should refuse to start rather than run against a configuration this build cannot
  /// rely on.
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
