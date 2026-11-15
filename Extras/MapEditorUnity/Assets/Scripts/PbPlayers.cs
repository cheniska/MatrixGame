using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// Player settings stored as map properties, with unrelated tokens preserved.
public sealed class PbPlayers
{
    public static readonly string[] Sides = { "Yellow", "Red", "Blue", "Green" };
    public static readonly string[] ResourceLabels =
        { "Titan", "Electronics", "Energy", "Plasma", "Production %" };
    public static readonly string[] AiLabels =
        { "Bomb delay, ms", "Strength", "Danger", "Bravery", "Teams", "Wait resources" };
    private static readonly string[] AiCodes = { "TBB", "SK", "DK", "BK", "TC", "WRK" };
    public readonly string[,] Resources = new string[4, 5];
    public readonly string[,] Ai = new string[4, 6];
    public string Maintenance;
    private readonly string[,] originalResources = new string[4, 5];
    private readonly string[,] originalAi = new string[4, 6];
    private readonly string originalMaintenance;
    private readonly string rawResources;
    private readonly string rawAi;

    public PbPlayers(PbStorage storage)
    {
        Dictionary<string, string> properties = storage.Properties();
        properties.TryGetValue("SideResInfo", out rawResources);
        properties.TryGetValue("SideAIInfo", out rawAi);
        properties.TryGetValue("MaintenanceTime", out originalMaintenance);
        Maintenance = originalMaintenance ?? "100";
        for (int side = 0; side < 4; side++)
        {
            for (int i = 0; i < 5; i++) Resources[side, i] = i == 4 ? "100" : "0";
            Ai[side, 0] = "60000";
            Ai[side, 1] = Ai[side, 2] = Ai[side, 5] = "1";
            Ai[side, 3] = "0.5";
            Ai[side, 4] = "1";
        }
        if (rawResources != null)
            foreach (string record in rawResources.Split('|'))
            {
                string[] cells = record.Split(',');
                if (!int.TryParse(cells[0], out int id) || id < 1 || id > 4) continue;
                for (int i = 0; i < 5 && i + 1 < cells.Length; i++)
                    Resources[id - 1, i] = cells[i + 1];
            }
        if (rawAi != null)
            foreach (string record in rawAi.Split('|'))
            {
                int colon = record.IndexOf(':');
                if (colon < 0) continue;
                int side = Array.IndexOf(Sides, record.Substring(0, colon));
                if (side < 0) continue;
                string[] tokens = record.Substring(colon + 1).Split('/');
                for (int i = 0; i + 1 < tokens.Length; i += 2)
                {
                    int field = Array.IndexOf(AiCodes, tokens[i]);
                    if (field >= 0) Ai[side, field] = tokens[i + 1];
                }
            }
        Array.Copy(Resources, originalResources, Resources.Length);
        Array.Copy(Ai, originalAi, Ai.Length);
    }

    public PbStorage Save(PbStorage source)
    {
        bool resourcesChanged = false, aiChanged = false;
        for (int side = 0; side < 4; side++)
        {
            for (int i = 0; i < 5; i++)
                if (Resources[side, i] != originalResources[side, i]) resourcesChanged = true;
            for (int i = 0; i < 6; i++)
                if (Ai[side, i] != originalAi[side, i]) aiChanged = true;
        }
        if (resourcesChanged)
        {
            if (rawResources == null) throw new InvalidOperationException("SideResInfo is absent.");
            string[] records = rawResources.Split('|');
            for (int r = 0; r < records.Length; r++)
            {
                string[] cells = records[r].Split(',');
                if (!int.TryParse(cells[0], out int id) || id < 1 || id > 4) continue;
                int side = id - 1;
                for (int i = 0; i < 5; i++)
                    if (Resources[side, i] != originalResources[side, i])
                    {
                        if (cells.Length <= i + 1) Array.Resize(ref cells, i + 2);
                        cells[i + 1] = Integer(Resources[side, i], ResourceLabels[i]);
                    }
                records[r] = string.Join(",", cells);
            }
            source = source.ReplaceProperty("SideResInfo", string.Join("|", records));
        }
        if (aiChanged)
        {
            if (rawAi == null) throw new InvalidOperationException("SideAIInfo is absent.");
            string[] records = rawAi.Split('|');
            for (int r = 0; r < records.Length; r++)
            {
                int colon = records[r].IndexOf(':');
                if (colon < 0) continue;
                int side = Array.IndexOf(Sides, records[r].Substring(0, colon));
                if (side < 0) continue;
                var tokens = new List<string>(records[r].Substring(colon + 1).Split('/'));
                for (int i = 0; i < 6; i++)
                {
                    if (Ai[side, i] == originalAi[side, i]) continue;
                    string value = i == 0 || i == 4
                        ? Integer(Ai[side, i], AiLabels[i]) : Decimal(Ai[side, i], AiLabels[i]);
                    int at = -1;
                    for (int t = 0; t + 1 < tokens.Count; t += 2)
                        if (tokens[t] == AiCodes[i]) { at = t + 1; break; }
                    if (at >= 0) tokens[at] = value;
                    else { tokens.Add(AiCodes[i]); tokens.Add(value); }
                }
                records[r] = Sides[side] + ":" + string.Join("/", tokens);
            }
            source = source.ReplaceProperty("SideAIInfo", string.Join("|", records));
        }
        if (Maintenance != originalMaintenance)
        {
            if (originalMaintenance == null)
                throw new InvalidOperationException("MaintenanceTime is absent.");
            source = source.ReplaceProperty("MaintenanceTime", Integer(Maintenance, "Maintenance %"));
        }
        return source;
    }

    private static string Integer(string value, string label)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed < 0)
            throw new InvalidOperationException(label + " must be a nonnegative integer.");
        return parsed.ToString(CultureInfo.InvariantCulture);
    }

    private static string Decimal(string value, string label)
    {
        if (!float.TryParse(value.Replace(',', '.'), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out float parsed) ||
            float.IsNaN(parsed) || float.IsInfinity(parsed) || parsed < 0f)
            throw new InvalidOperationException(label + " must be a nonnegative number.");
        return parsed.ToString("G9", CultureInfo.InvariantCulture);
    }
}
