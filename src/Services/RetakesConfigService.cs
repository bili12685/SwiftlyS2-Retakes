using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using SwiftlyS2.Shared;
using SwiftlyS2_Retakes.Configuration;
using SwiftlyS2_Retakes.Interfaces;
using SwiftlyS2_Retakes.Logging;

namespace SwiftlyS2_Retakes.Services;

public sealed class RetakesConfigService : IRetakesConfigService
{
  private readonly ISwiftlyCore _core;
  private readonly ILogger _logger;
  private readonly string _path;
  private readonly ConVarApplicator _conVarApplicator;
  private readonly RetakesCfgGenerator _cfgGenerator;

  public string ConfigPath => _path;

  private const string ConfigFileName = "config.json";
  private const string SectionName = "retakes";

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
  };

  public RetakesConfig Config { get; private set; } = new();

  /// <summary>
  /// Schema version the config file declared. A config with no version field reports
  /// <see cref="RetakesConfig.PreVersioningVersion"/> -- that is what such a config is.
  /// Left at that value when the field is present but not a number;
  /// <see cref="IsConfigVersionMalformed"/> distinguishes the two.
  /// </summary>
  public int DeclaredConfigVersion { get; private set; } = RetakesConfig.PreVersioningVersion;

  /// <summary>
  /// True when the config carries a version field that is not a number. Reported
  /// separately from a missing one so the refusal can say which is wrong.
  /// </summary>
  public bool IsConfigVersionMalformed { get; private set; }

  /// <summary>
  /// Whether the config declares an acceptable schema version. Strict by design: the
  /// field must be present, numeric, and at least <see cref="RetakesConfig.MinimumSupportedVersion"/>.
  /// A config predating versioning has no field and is refused along with an
  /// out-of-date or malformed one -- nothing is repaired or stamped, so a config never
  /// quietly passes without declaring what it is.
  /// </summary>
  public bool IsConfigVersionSupported =>
    !IsConfigVersionMalformed &&
    DeclaredConfigVersion >= RetakesConfig.MinimumSupportedVersion;

  public RetakesConfigService(ISwiftlyCore core, ILogger logger)
  {
    _core = core;
    _logger = logger;
    _conVarApplicator = new ConVarApplicator(core);
    _cfgGenerator = new RetakesCfgGenerator(core, logger);

    _path = _core.Configuration.GetConfigPath(ConfigFileName);
    TrySanitizeConfigJsonFile();

    _core.Configuration.InitializeJsonWithModel<RetakesConfig>(ConfigFileName, SectionName);
    TrySanitizeConfigJsonFile();

    _core.Configuration.Configure(builder =>
    {
      builder.AddJsonFile(_path, optional: false, reloadOnChange: false);
    });
  }

  private void TrySanitizeConfigJsonFile()
  {
    try
    {
      if (!File.Exists(_path)) return;

      var text = File.ReadAllText(_path);
      if (string.IsNullOrWhiteSpace(text)) return;

      // Accept comments and trailing commas here, matching what the Swiftly/MEI
      // provider already tolerates when it reads this file. Without this the sanitizers
      // silently no-op on a hand-edited config, and for a legacy Weapons.Pistols array
      // that means the bind fails and the loader's catch-all replaces the whole
      // configuration with defaults.
      var node = JsonNode.Parse(text, nodeOptions: null, documentOptions: new JsonDocumentOptions
      {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
      });
      if (node is not JsonObject rootObj) return;

      // Read the declared version, never repair it: a missing or malformed value is
      // precisely what the startup check exists to catch, so rewriting one here would
      // hide the problem instead of surfacing it.
      switch (ConfigSanitizer.ReadConfigVersion(rootObj, SectionName, out var declared))
      {
        case ConfigSanitizer.ConfigVersionField.Declared:
          DeclaredConfigVersion = declared;
          IsConfigVersionMalformed = false;
          break;

        case ConfigSanitizer.ConfigVersionField.Malformed:
          IsConfigVersionMalformed = true;
          break;

        default:
          DeclaredConfigVersion = RetakesConfig.PreVersioningVersion;
          IsConfigVersionMalformed = false;
          break;
      }

      var changed = ConfigSanitizer.SanitizeAll(rootObj, SectionName);
      if (!changed) return;

      var updated = rootObj.ToJsonString(JsonOptions);
      File.WriteAllText(_path, updated);
      _logger.LogPluginWarning("Retakes: config.json was sanitized (upgraded legacy keys or removed ':' keys)");
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Retakes: failed to sanitize config.json before loading");
    }
  }

  public void LoadOrCreate()
  {
    try
    {
      if (_core.Configuration.Manager is IConfigurationRoot root)
      {
          root.Reload();
      }
      _logger.LogPluginDebug("Retakes: Swiftly config base path: {Base}", _core.Configuration.BasePath);
      _logger.LogPluginDebug("Retakes: config.json path: {Path}", _path);

      if (!_core.Configuration.BasePathExists)
      {
        _logger.LogPluginWarning("Retakes: Swiftly config base path does not exist yet: {Base}", _core.Configuration.BasePath);
      }

      var section = _core.Configuration.Manager.GetSection(SectionName);
      var cfg = section.Get<RetakesConfig>();
      Config = cfg ?? new RetakesConfig();

      if (cfg is null)
      {
        _logger.LogPluginWarning("Retakes: config section '{Section}' was not found or could not be parsed. Config will use defaults.", SectionName);
      }

      if (!File.Exists(_path))
      {
        _logger.LogPluginWarning("Retakes: config.json was not found after initialization. Expected at {Path}", _path);
      }

      EnsureTeamBalanceConfigPresent();
      EnsureSmokeScenariosConfigPresent();
      EnsureSoloBotConfigPresent();
      EnsureAfkManagerConfigPresent();
      EnsureWeaponDefaultsConfigPresent();
      ApplyLoggingToggles(Config.Server);
    }
    catch (Exception ex)
    {
      _logger.LogPluginError(ex, "Retakes: failed to load config.json from {Path}", _path);
      Config = new RetakesConfig();
    }
  }

  private void EnsureTeamBalanceConfigPresent()
  {
    try
    {
      if (!File.Exists(_path))
      {
        return;
      }

      var text = File.ReadAllText(_path);
      if (string.IsNullOrWhiteSpace(text))
      {
        return;
      }

      var rootNode = JsonNode.Parse(text);
      if (rootNode is not JsonObject rootObj)
      {
        return;
      }

      // Ensure we never keep colon-delimited keys alongside nested objects.
      ConfigSanitizer.SanitizeColonDelimitedKeys(rootObj);
      ConfigSanitizer.SanitizeCaseInsensitiveDuplicateKeys(rootObj);

      if (rootObj[SectionName] is not JsonObject sectionObj)
      {
        sectionObj = new JsonObject();
        rootObj[SectionName] = sectionObj;
      }

      var teamBalanceKey = sectionObj.ContainsKey("TeamBalance") ? "TeamBalance"
        : sectionObj.ContainsKey("teamBalance") ? "teamBalance"
        : "TeamBalance";

      if (sectionObj[teamBalanceKey] is not JsonObject teamBalanceObj)
      {
        teamBalanceObj = new JsonObject();
        sectionObj[teamBalanceKey] = teamBalanceObj;
      }

      string Key(string pascal, string camel) => teamBalanceObj.ContainsKey(pascal) ? pascal : teamBalanceObj.ContainsKey(camel) ? camel : pascal;

      if (teamBalanceObj[Key("Enabled", "enabled")] is null) teamBalanceObj[Key("Enabled", "enabled")] = Config.TeamBalance.Enabled;
      if (teamBalanceObj[Key("TerroristRatio", "terroristRatio")] is null) teamBalanceObj[Key("TerroristRatio", "terroristRatio")] = Config.TeamBalance.TerroristRatio;
      if (teamBalanceObj[Key("ForceEvenWhenPlayersMod10", "forceEvenWhenPlayersMod10")] is null) teamBalanceObj[Key("ForceEvenWhenPlayersMod10", "forceEvenWhenPlayersMod10")] = Config.TeamBalance.ForceEvenWhenPlayersMod10;
      if (teamBalanceObj[Key("IncludeBots", "includeBots")] is null) teamBalanceObj[Key("IncludeBots", "includeBots")] = Config.TeamBalance.IncludeBots;
      if (teamBalanceObj[Key("SkillBasedEnabled", "skillBasedEnabled")] is null) teamBalanceObj[Key("SkillBasedEnabled", "skillBasedEnabled")] = Config.TeamBalance.SkillBasedEnabled;

      if (teamBalanceObj[Key("ScrambleEnabled", "scrambleEnabled")] is null) teamBalanceObj[Key("ScrambleEnabled", "scrambleEnabled")] = Config.TeamBalance.ScrambleEnabled;
      if (teamBalanceObj[Key("RoundsToScramble", "roundsToScramble")] is null) teamBalanceObj[Key("RoundsToScramble", "roundsToScramble")] = Config.TeamBalance.RoundsToScramble;

      var updated = rootObj.ToJsonString(JsonOptions);
      File.WriteAllText(_path, updated);
    }
    catch (Exception ex)
    {
      _logger.LogPluginWarning(ex, "Retakes: failed to ensure TeamBalance exists in config.json");
    }
  }

  private void EnsureSmokeScenariosConfigPresent()
  {
    try
    {
      if (!File.Exists(_path))
      {
        return;
      }

      var text = File.ReadAllText(_path);
      if (string.IsNullOrWhiteSpace(text))
      {
        return;
      }

      var rootNode = JsonNode.Parse(text);
      if (rootNode is not JsonObject rootObj)
      {
        return;
      }

      ConfigSanitizer.SanitizeColonDelimitedKeys(rootObj);
      ConfigSanitizer.SanitizeCaseInsensitiveDuplicateKeys(rootObj);

      if (rootObj[SectionName] is not JsonObject sectionObj)
      {
        sectionObj = new JsonObject();
        rootObj[SectionName] = sectionObj;
      }

      var smokeScenariosKey = sectionObj.ContainsKey("SmokeScenarios") ? "SmokeScenarios"
        : sectionObj.ContainsKey("smokeScenarios") ? "smokeScenarios"
        : "SmokeScenarios";

      if (sectionObj[smokeScenariosKey] is not JsonObject smokeScenariosObj)
      {
        smokeScenariosObj = new JsonObject();
        sectionObj[smokeScenariosKey] = smokeScenariosObj;
      }

      string Key(string pascal, string camel) => smokeScenariosObj.ContainsKey(pascal) ? pascal : smokeScenariosObj.ContainsKey(camel) ? camel : pascal;

      if (smokeScenariosObj[Key("Enabled", "enabled")] is null) smokeScenariosObj[Key("Enabled", "enabled")] = Config.SmokeScenarios.Enabled;
      if (smokeScenariosObj[Key("RandomRoundsEnabled", "randomRoundsEnabled")] is null) smokeScenariosObj[Key("RandomRoundsEnabled", "randomRoundsEnabled")] = Config.SmokeScenarios.RandomRoundsEnabled;
      if (smokeScenariosObj[Key("RandomRoundChance", "randomRoundChance")] is null) smokeScenariosObj[Key("RandomRoundChance", "randomRoundChance")] = Config.SmokeScenarios.RandomRoundChance;

      var updated = rootObj.ToJsonString(JsonOptions);
      File.WriteAllText(_path, updated);
    }
    catch (Exception ex)
    {
      _logger.LogPluginWarning(ex, "Retakes: failed to ensure SmokeScenarios exists in config.json");
    }
  }

  private void EnsureSoloBotConfigPresent()
  {
    try
    {
      if (!File.Exists(_path))
      {
        return;
      }

      var text = File.ReadAllText(_path);
      if (string.IsNullOrWhiteSpace(text))
      {
        return;
      }

      var rootNode = JsonNode.Parse(text);
      if (rootNode is not JsonObject rootObj)
      {
        return;
      }

      ConfigSanitizer.SanitizeColonDelimitedKeys(rootObj);
      ConfigSanitizer.SanitizeCaseInsensitiveDuplicateKeys(rootObj);

      if (rootObj[SectionName] is not JsonObject sectionObj)
      {
        sectionObj = new JsonObject();
        rootObj[SectionName] = sectionObj;
      }

      var soloBotKey = sectionObj.ContainsKey("SoloBot") ? "SoloBot"
        : sectionObj.ContainsKey("soloBot") ? "soloBot"
        : "SoloBot";

      if (sectionObj[soloBotKey] is not JsonObject soloBotObj)
      {
        soloBotObj = new JsonObject();
        sectionObj[soloBotKey] = soloBotObj;
      }

      string Key(string pascal, string camel) => soloBotObj.ContainsKey(pascal) ? pascal : soloBotObj.ContainsKey(camel) ? camel : pascal;

      if (soloBotObj[Key("Enabled", "enabled")] is null) soloBotObj[Key("Enabled", "enabled")] = Config.SoloBot.Enabled;
      if (soloBotObj[Key("Difficulty", "difficulty")] is null) soloBotObj[Key("Difficulty", "difficulty")] = Config.SoloBot.Difficulty;

      var updated = rootObj.ToJsonString(JsonOptions);
      File.WriteAllText(_path, updated);
    }
    catch (Exception ex)
    {
      _logger.LogPluginWarning(ex, "Retakes: failed to ensure SoloBot exists in config.json");
    }
  }

  private void EnsureAfkManagerConfigPresent()
  {
    try
    {
      if (!File.Exists(_path))
      {
        return;
      }

      var text = File.ReadAllText(_path);
      if (string.IsNullOrWhiteSpace(text))
      {
        return;
      }

      var rootNode = JsonNode.Parse(text);
      if (rootNode is not JsonObject rootObj)
      {
        return;
      }

      ConfigSanitizer.SanitizeColonDelimitedKeys(rootObj);
      ConfigSanitizer.SanitizeCaseInsensitiveDuplicateKeys(rootObj);

      if (rootObj[SectionName] is not JsonObject sectionObj)
      {
        sectionObj = new JsonObject();
        rootObj[SectionName] = sectionObj;
      }

      var afkManagerKey = sectionObj.ContainsKey("AfkManager") ? "AfkManager"
        : sectionObj.ContainsKey("afkManager") ? "afkManager"
        : "AfkManager";

      if (sectionObj[afkManagerKey] is not JsonObject afkManagerObj)
      {
        afkManagerObj = new JsonObject();
        sectionObj[afkManagerKey] = afkManagerObj;
      }

      string Key(string pascal, string camel) => afkManagerObj.ContainsKey(pascal) ? pascal : afkManagerObj.ContainsKey(camel) ? camel : pascal;

      if (afkManagerObj[Key("Enabled", "enabled")] is null) afkManagerObj[Key("Enabled", "enabled")] = Config.AfkManager.Enabled;
      if (afkManagerObj[Key("IdleSecondsBeforeSpectator", "idleSecondsBeforeSpectator")] is null) afkManagerObj[Key("IdleSecondsBeforeSpectator", "idleSecondsBeforeSpectator")] = Config.AfkManager.IdleSecondsBeforeSpectator;
      if (afkManagerObj[Key("SpectatorSecondsBeforeKick", "spectatorSecondsBeforeKick")] is null) afkManagerObj[Key("SpectatorSecondsBeforeKick", "spectatorSecondsBeforeKick")] = Config.AfkManager.SpectatorSecondsBeforeKick;
      if (afkManagerObj[Key("MovementDistanceThreshold", "movementDistanceThreshold")] is null) afkManagerObj[Key("MovementDistanceThreshold", "movementDistanceThreshold")] = Config.AfkManager.MovementDistanceThreshold;
      if (afkManagerObj[Key("CheckIntervalSeconds", "checkIntervalSeconds")] is null) afkManagerObj[Key("CheckIntervalSeconds", "checkIntervalSeconds")] = Config.AfkManager.CheckIntervalSeconds;
      if (afkManagerObj[Key("KickReason", "kickReason")] is null) afkManagerObj[Key("KickReason", "kickReason")] = Config.AfkManager.KickReason;

      var updated = rootObj.ToJsonString(JsonOptions);
      File.WriteAllText(_path, updated);
    }
    catch (Exception ex)
    {
      _logger.LogPluginWarning(ex, "Retakes: failed to ensure AfkManager exists in config.json");
    }
  }

  private void EnsureWeaponDefaultsConfigPresent()
  {
    try
    {
      if (!File.Exists(_path))
      {
        return;
      }

      var text = File.ReadAllText(_path);
      if (string.IsNullOrWhiteSpace(text))
      {
        return;
      }

      var rootNode = JsonNode.Parse(text);
      if (rootNode is not JsonObject rootObj)
      {
        return;
      }

      ConfigSanitizer.SanitizeColonDelimitedKeys(rootObj);
      ConfigSanitizer.SanitizeCaseInsensitiveDuplicateKeys(rootObj);

      if (rootObj[SectionName] is not JsonObject sectionObj)
      {
        sectionObj = new JsonObject();
        rootObj[SectionName] = sectionObj;
      }

      var weaponsKey = sectionObj.ContainsKey("Weapons") ? "Weapons"
        : sectionObj.ContainsKey("weapons") ? "weapons"
        : "Weapons";

      if (sectionObj[weaponsKey] is not JsonObject weaponsObj)
      {
        weaponsObj = new JsonObject();
        sectionObj[weaponsKey] = weaponsObj;
      }

      var defaultsKey = weaponsObj.ContainsKey("Defaults") ? "Defaults"
        : weaponsObj.ContainsKey("defaults") ? "defaults"
        : "Defaults";

      if (weaponsObj[defaultsKey] is not JsonObject defaultsObj)
      {
        defaultsObj = new JsonObject();
        weaponsObj[defaultsKey] = defaultsObj;
      }

      EnsureDefaultRoundLoadout(defaultsObj, "Pistol", "pistol", Config.Weapons.Defaults.Pistol);
      EnsureDefaultRoundLoadout(defaultsObj, "HalfBuy", "halfBuy", Config.Weapons.Defaults.HalfBuy);
      EnsureDefaultRoundLoadout(defaultsObj, "FullBuy", "fullBuy", Config.Weapons.Defaults.FullBuy);

      var updated = rootObj.ToJsonString(JsonOptions);
      File.WriteAllText(_path, updated);
    }
    catch (Exception ex)
    {
      _logger.LogPluginWarning(ex, "Retakes: failed to ensure weapon defaults exist in config.json");
    }
  }

  private static void EnsureDefaultRoundLoadout(JsonObject parent, string pascalName, string camelName, DefaultRoundLoadoutConfig defaults)
  {
    var roundKey = parent.ContainsKey(pascalName) ? pascalName
      : parent.ContainsKey(camelName) ? camelName
      : pascalName;

    if (parent[roundKey] is not JsonObject roundObj)
    {
      roundObj = new JsonObject();
      parent[roundKey] = roundObj;
    }

    EnsureDefaultWeaponSelection(roundObj, "Primary", "primary", defaults.Primary);
    EnsureDefaultWeaponSelection(roundObj, "Secondary", "secondary", defaults.Secondary);
  }

  private static void EnsureDefaultWeaponSelection(JsonObject parent, string pascalName, string camelName, DefaultWeaponSelectionConfig defaults)
  {
    var selectionKey = parent.ContainsKey(pascalName) ? pascalName
      : parent.ContainsKey(camelName) ? camelName
      : pascalName;

    if (parent[selectionKey] is not JsonObject selectionObj)
    {
      selectionObj = new JsonObject();
      parent[selectionKey] = selectionObj;
    }

    string Key(string pascal, string camel) => selectionObj.ContainsKey(pascal) ? pascal : selectionObj.ContainsKey(camel) ? camel : pascal;

    if (selectionObj[Key("T", "t")] is null) selectionObj[Key("T", "t")] = defaults.T;
    if (selectionObj[Key("Ct", "ct")] is null) selectionObj[Key("Ct", "ct")] = defaults.Ct;
  }

  public void Save()
  {
    try
    {
      var dir = Path.GetDirectoryName(_path);
      if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
      {
        Directory.CreateDirectory(dir);
      }

      var wrapped = new System.Collections.Generic.Dictionary<string, object?>
      {
        [SectionName] = Config
      };

      var json = JsonSerializer.Serialize(wrapped, JsonOptions);
      File.WriteAllText(_path, json);
      _logger.LogPluginInformation("Retakes: config.json saved to {Path}", _path);
    }
    catch (Exception ex)
    {
      _logger.LogPluginError(ex, "Retakes: failed to save config.json to {Path}", _path);
    }
  }

  public void ApplyToConvars(bool restartGame = false)
  {
    _core.Scheduler.NextTick(() =>
    {
      _conVarApplicator.ApplyConfig(Config);
      _cfgGenerator.Apply(Config, restartGame);
      ApplyLoggingToggles(Config.Server);
    });
  }

  private static void ApplyLoggingToggles(ServerConfig server)
  {
    LoggingToggle.DebugEnabled = server.DebugEnabled;
  }
}
