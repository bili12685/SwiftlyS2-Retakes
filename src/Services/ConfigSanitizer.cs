using System.Text.Json.Nodes;

namespace SwiftlyS2_Retakes.Services;

/// <summary>
/// Utility class for sanitizing JSON configuration files.
/// Handles colon-delimited keys, case-insensitive duplicates, and section normalization.
/// </summary>
public static class ConfigSanitizer
{
  /// <summary>
  /// Normalizes the section key to the expected casing.
  /// </summary>
  public static bool NormalizeSectionKey(JsonObject rootObj, string sectionName)
  {
    var existingExact = rootObj.ContainsKey(sectionName);
    var otherKey = rootObj.Select(kvp => kvp.Key)
      .FirstOrDefault(k => string.Equals(k, sectionName, StringComparison.OrdinalIgnoreCase) && !string.Equals(k, sectionName, StringComparison.Ordinal));

    if (otherKey is null)
    {
      return false;
    }

    if (!rootObj.TryGetPropertyValue(otherKey, out var otherNode) || otherNode is null)
    {
      rootObj.Remove(otherKey);
      return true;
    }

    if (!existingExact)
    {
      rootObj[sectionName] = otherNode;
      rootObj.Remove(otherKey);
      return true;
    }

    if (rootObj[sectionName] is JsonObject winnerObj && otherNode is JsonObject loserObj)
    {
      foreach (var kvp in loserObj.ToList())
      {
        if (!winnerObj.TryGetPropertyValue(kvp.Key, out var existing) || existing is null)
        {
          winnerObj[kvp.Key] = kvp.Value?.DeepClone();
        }
      }
    }

    rootObj.Remove(otherKey);
    return true;
  }

  /// <summary>
  /// Converts colon-delimited keys (e.g., "section:key") into nested JSON objects.
  /// </summary>
  public static bool SanitizeColonDelimitedKeys(JsonObject obj)
  {
    var changed = false;

    var colonKeys = obj.Select(kvp => kvp.Key)
      .Where(k => k.Contains(':', StringComparison.Ordinal))
      .ToList();

    foreach (var key in colonKeys)
    {
      if (!obj.TryGetPropertyValue(key, out var valueNode) || valueNode is null) continue;

      var parts = key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      if (parts.Length < 2) continue;

      JsonObject cursor = obj;
      for (var i = 0; i < parts.Length - 1; i++)
      {
        var part = parts[i];
        if (cursor[part] is not JsonObject next)
        {
          next = new JsonObject();
          cursor[part] = next;
          changed = true;
        }
        cursor = next;
      }

      var leaf = parts[^1];
      if (cursor[leaf] is null)
      {
        cursor[leaf] = valueNode.DeepClone();
        changed = true;
      }

      obj.Remove(key);
      changed = true;
    }

    foreach (var kvp in obj.ToList())
    {
      if (kvp.Value is JsonObject child)
      {
        if (SanitizeColonDelimitedKeys(child)) changed = true;
      }
    }

    return changed;
  }

  /// <summary>
  /// Merges case-insensitive duplicate keys, keeping the first occurrence.
  /// </summary>
  public static bool SanitizeCaseInsensitiveDuplicateKeys(JsonObject obj)
  {
    var changed = false;

    static bool IsAllLower(string s)
    {
      foreach (var ch in s)
      {
        if (char.IsLetter(ch) && char.IsUpper(ch)) return false;
      }
      return true;
    }

    void MergeInto(JsonNode? winner, JsonNode? loser)
    {
      if (winner is JsonObject winnerObj && loser is JsonObject loserObj)
      {
        foreach (var kvp in loserObj.ToList())
        {
          if (!winnerObj.TryGetPropertyValue(kvp.Key, out var existing) || existing is null)
          {
            winnerObj[kvp.Key] = kvp.Value?.DeepClone();
            changed = true;
            continue;
          }

          MergeInto(existing, kvp.Value);
        }

        return;
      }
    }

    var keys = obj.Select(kvp => kvp.Key).ToList();
    var groups = keys.GroupBy(k => k.ToLowerInvariant()).Where(g => g.Count() > 1).ToList();

    foreach (var group in groups)
    {
      var groupKeys = group.ToList();
      var winnerKey = groupKeys
        .OrderBy(k => IsAllLower(k) ? 1 : 0)
        .ThenBy(k => k, StringComparer.Ordinal)
        .First();

      if (!obj.TryGetPropertyValue(winnerKey, out var winnerNode)) continue;

      foreach (var loserKey in groupKeys.Where(k => !string.Equals(k, winnerKey, StringComparison.Ordinal)).ToList())
      {
        if (!obj.TryGetPropertyValue(loserKey, out var loserNode))
        {
          obj.Remove(loserKey);
          changed = true;
          continue;
        }

        MergeInto(winnerNode, loserNode);
        obj.Remove(loserKey);
        changed = true;
      }
    }

    foreach (var kvp in obj.ToList())
    {
      if (kvp.Value is JsonObject child)
      {
        if (SanitizeCaseInsensitiveDuplicateKeys(child)) changed = true;
      }
    }

    return changed;
  }

  /// <summary>
  /// Runs all sanitization passes on the JSON object.
  /// </summary>
  public static bool SanitizeAll(JsonObject rootObj, string sectionName, int configVersion)
  {
    var changed = MigrateLegacyPistolShape(rootObj, sectionName);
    changed |= EnsureConfigVersion(rootObj, sectionName, configVersion);
    changed |= SanitizeColonDelimitedKeys(rootObj);
    changed |= SanitizeCaseInsensitiveDuplicateKeys(rootObj);
    changed |= NormalizeSectionKey(rootObj, sectionName);
    return changed;
  }

  /// <summary>
  /// Reads the schema version the section declares. Returns false when the section is
  /// missing, or declares no version, or declares something that is not a number.
  /// </summary>
  public static bool TryGetConfigVersion(JsonObject rootObj, string sectionName, out int version)
  {
    version = 0;

    var sectionKey = FindKey(rootObj, sectionName);
    if (sectionKey is null || rootObj[sectionKey] is not JsonObject section) return false;

    var versionKey = FindKey(section, "ConfigVersion");
    if (versionKey is null) return false;

    return section[versionKey] is JsonValue value && value.TryGetValue(out version);
  }

  /// <summary>
  /// Stamps the schema version when the section does not declare one, inserting it as
  /// the first property so it sits at the top of the section.
  /// </summary>
  /// <remarks>
  /// A config predating versioning has no field. It is stamped rather than treated as
  /// version 0, which would otherwise be below every supported minimum and reject the
  /// config on the first load after an upgrade.
  /// </remarks>
  public static bool EnsureConfigVersion(JsonObject rootObj, string sectionName, int configVersion)
  {
    var sectionKey = FindKey(rootObj, sectionName);
    if (sectionKey is null || rootObj[sectionKey] is not JsonObject section) return false;

    var versionKey = FindKey(section, "ConfigVersion");

    // A valid declared version is never rewritten, even if we disagree with it.
    if (versionKey is not null &&
        section[versionKey] is JsonValue declared &&
        declared.TryGetValue<int>(out _))
    {
      return false;
    }

    // Missing, or present but not a number. A non-numeric value would fail the bind and
    // the loader's catch-all would then replace the entire configuration with defaults,
    // so it is repaired here rather than left alone.
    //
    // JsonObject keeps insertion order but cannot prepend, so rebuild with the version
    // first and drop the old entry. Remove before re-adding: a node that still has a
    // parent cannot be adopted.
    var rebuilt = new JsonObject { ["ConfigVersion"] = configVersion };
    foreach (var key in section.Select(kvp => kvp.Key).ToList())
    {
      if (versionKey is not null && string.Equals(key, versionKey, StringComparison.Ordinal))
      {
        continue;
      }

      var value = section[key];
      section.Remove(key);
      rebuilt[key] = value;
    }

    rootObj[sectionKey] = rebuilt;
    return true;
  }

  /// <summary>
  /// Rewrites the legacy flat <c>Weapons.Pistols</c> array into the current
  /// <c>{"All": [...]}</c> shape.
  /// </summary>
  /// <remarks>
  /// This runs before the configuration is bound. Without it an existing config.json
  /// fails to bind, and the loader's catch-all replaces the whole configuration with
  /// defaults -- taking every unrelated setting down with it, not just the pistols.
  /// </remarks>
  public static bool MigrateLegacyPistolShape(JsonObject rootObj, string sectionName)
  {
    var sectionKey = FindKey(rootObj, sectionName);
    if (sectionKey is null || rootObj[sectionKey] is not JsonObject section) return false;

    var weaponsKey = FindKey(section, "Weapons");
    if (weaponsKey is null || section[weaponsKey] is not JsonObject weapons) return false;

    var pistolsKey = FindKey(weapons, "Pistols");
    // Absent entirely, or already the object shape: nothing to migrate.
    if (pistolsKey is null || weapons[pistolsKey] is not JsonArray legacy) return false;

    weapons[pistolsKey] = new JsonObject { ["All"] = legacy.DeepClone() };
    return true;
  }

  /// <summary>
  /// Finds a property by name, ignoring case, so a hand-edited key is still migrated
  /// rather than silently left in the legacy shape.
  /// </summary>
  private static string? FindKey(JsonObject obj, string name) =>
    obj.Select(kvp => kvp.Key)
       .FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
}
