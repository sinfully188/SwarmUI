using FreneticUtilities.FreneticExtensions;
using FreneticUtilities.FreneticToolkit;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using System.IO;
using System.Text.RegularExpressions;

namespace MissingLoraHandlerExtension;

/// <summary>Extension that adds auto-resolution tools for selected missing LoRAs.</summary>
public class MissingLoraHandlerExtension : Extension
{
    /// <summary>Register the extension web assets.</summary>
    public override void OnPreInit()
    {
        ScriptFiles.Add("Assets/missing_lora_handler.js");
        StyleSheetFiles.Add("Assets/missing_lora_handler.css");
    }

    /// <summary>Register API routes used by the extension.</summary>
    public override void OnInit()
    {
        API.RegisterAPICall(ResolveMissingLora, false, Permissions.FundamentalModelAccess);
    }

    /// <summary>Normalize a model lookup string to the server's slash and trim conventions.</summary>
    public static string NormalizeModelLookupName(string modelName)
    {
        modelName = (modelName ?? "").Replace('\\', '/').Trim();
        while (modelName.Contains("//"))
        {
            modelName = modelName.Replace("//", "/");
        }
        return modelName.TrimStart('/');
    }

    /// <summary>Clean a model name the same way the web client does.</summary>
    public static string CleanModelNameLikeClient(string modelName)
    {
        modelName = NormalizeModelLookupName(modelName);
        if (modelName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
        {
            return modelName[..^".safetensors".Length];
        }
        return modelName;
    }

    /// <summary>Extract the filename leaf from a model path.</summary>
    public static string ExtractLeafName(string modelName)
    {
        modelName = (modelName ?? "").Trim();
        int slash = modelName.LastIndexOf('/');
        int backslash = modelName.LastIndexOf('\\');
        int split = Math.Max(slash, backslash);
        return split >= 0 ? modelName[(split + 1)..] : modelName;
    }

    /// <summary>Remove a known model extension from the filename leaf, if present.</summary>
    public static string RemoveKnownModelExtension(string modelName)
    {
        modelName = NormalizeModelLookupName(modelName);
        string fileName = ExtractLeafName(modelName);
        string extension = Path.GetExtension(fileName).TrimStart('.').ToLowerFast();
        if (!string.IsNullOrWhiteSpace(extension)
            && (T2IModel.NativelySupportedModelExtensions.Contains(extension) || T2IModel.LegacyModelExtensions.Contains(extension)))
        {
            return fileName[..^(extension.Length + 1)];
        }
        return fileName;
    }

    /// <summary>Normalize a model leaf for case-insensitive filename matching.</summary>
    public static string NormalizeModelLeaf(string modelName)
    {
        return RemoveKnownModelExtension(modelName).ToLowerFast();
    }

    /// <summary>Normalize free-form text to a simple alphanumeric token for fuzzy comparisons.</summary>
    public static string NormalizeLooseLookupToken(string text)
    {
        return Regex.Replace((text ?? "").ToLowerFast(), "[^a-z0-9]+", "");
    }

    /// <summary>Resolve a user-allowed LoRA model directly from the current handler cache.</summary>
    public static T2IModel TryResolveAllowedModel(Session session, T2IModelHandler handler, string modelName)
    {
        string normalized = NormalizeModelLookupName(modelName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }
        HashSet<string> candidates = [normalized, CleanModelNameLikeClient(normalized)];
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !session.User.IsAllowedModel(candidate))
            {
                continue;
            }
            if (handler.Models.TryGetValue(candidate, out T2IModel match))
            {
                return match;
            }
            if (handler.Models.TryGetValue($"{candidate}.safetensors", out match))
            {
                return match;
            }
        }
        return null;
    }

    /// <summary>Build a successful LoRA resolution response.</summary>
    public static JObject LoraResolveResult(T2IModel model, string matchMode)
    {
        return new JObject()
        {
            ["found"] = true,
            ["resolved_name"] = CleanModelNameLikeClient(model.Name),
            ["resolved_full_name"] = model.Name,
            ["match_mode"] = matchMode,
            ["lora_default_weight"] = model.Metadata?.LoraDefaultWeight ?? "",
            ["lora_default_confinement"] = model.Metadata?.LoraDefaultConfinement ?? ""
        };
    }

    /// <summary>Build an ambiguous LoRA resolution response.</summary>
    public static JObject AmbiguousLoraResolveResult(List<T2IModel> matches, string matchMode)
    {
        return new JObject()
        {
            ["error"] = $"Multiple LoRAs matched by {matchMode}; unable to auto-fix safely.",
            ["matches"] = JArray.FromObject(matches.Take(20).Select(m => CleanModelNameLikeClient(m.Name)).ToList())
        };
    }

    [API.APIDescription("Attempts to find a moved LoRA by matching its filename within the LoRA folders, ignoring the original subfolder.",
        "\"resolved_name\": \"folder/model_name\"")]
    /// <summary>Attempt to resolve a missing LoRA to a currently available LoRA path.</summary>
    public static async Task<JObject> ResolveMissingLora(Session session,
        [API.APIParameter("The LoRA name/path to resolve.")] string loraName)
    {
        if (!Program.T2IModelSets.TryGetValue("LoRA", out T2IModelHandler handler))
        {
            return new JObject() { ["error"] = "LoRA models are not available." };
        }
        string normalized = NormalizeModelLookupName(loraName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new JObject() { ["error"] = "LoRA name is required." };
        }
        string targetLeaf = NormalizeModelLeaf(normalized);
        string looseTargetPreview = NormalizeLooseLookupToken(RemoveKnownModelExtension(normalized));
        Logs.Debug($"ResolveMissingLora request: input='{loraName}', normalized='{normalized}', leaf='{targetLeaf}', loose='{looseTargetPreview}'");
        List<T2IModel> allowedModels;
        using (ManyReadOneWriteLock.ReadClaim claim = Program.RefreshLock.LockRead())
        {
            T2IModel directMatch = TryResolveAllowedModel(session, handler, normalized);
            if (directMatch is not null)
            {
                Logs.Debug($"ResolveMissingLora exact match found: '{normalized}' -> '{directMatch.Name}'");
                return LoraResolveResult(directMatch, "exact");
            }
            allowedModels = [.. handler.Models.Values.Where(m => session.User.IsAllowedModel(m.Name))];
            Logs.Debug($"ResolveMissingLora searching {allowedModels.Count} allowed LoRAs by filename leaf '{targetLeaf}'");
            List<T2IModel> leafMatches = [.. allowedModels.Where(m => NormalizeModelLeaf(m.Name) == targetLeaf)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
            if (leafMatches.Count == 1)
            {
                Logs.Debug($"ResolveMissingLora filename match found: '{targetLeaf}' -> '{leafMatches[0].Name}'");
                return LoraResolveResult(leafMatches[0], "filename");
            }
            if (leafMatches.Count > 1)
            {
                Logs.Debug($"ResolveMissingLora filename search ambiguous for '{targetLeaf}': {string.Join(", ", leafMatches.Take(10).Select(m => m.Name))}");
                return AmbiguousLoraResolveResult(leafMatches, "filename");
            }
            if (!string.IsNullOrWhiteSpace(looseTargetPreview))
            {
                Logs.Debug($"ResolveMissingLora searching titles by loose token '{looseTargetPreview}'");
                List<T2IModel> titleMatches = [.. allowedModels.Where(m =>
                {
                    string title = m.Metadata?.Title;
                    return !string.IsNullOrWhiteSpace(title) && NormalizeLooseLookupToken(title) == looseTargetPreview;
                }).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
                if (titleMatches.Count == 1)
                {
                    Logs.Debug($"ResolveMissingLora title match found: '{looseTargetPreview}' -> '{titleMatches[0].Name}'");
                    return LoraResolveResult(titleMatches[0], "title");
                }
                if (titleMatches.Count > 1)
                {
                    Logs.Debug($"ResolveMissingLora title search ambiguous for '{looseTargetPreview}': {string.Join(", ", titleMatches.Take(10).Select(m => m.Name))}");
                    return AmbiguousLoraResolveResult(titleMatches, "title");
                }
            }
        }
        List<string> diskMatches = [];
        bool isSupportedModelFile(string path)
        {
            string extension = Path.GetExtension(path).TrimStart('.').ToLowerFast();
            return T2IModel.NativelySupportedModelExtensions.Contains(extension) || T2IModel.LegacyModelExtensions.Contains(extension);
        }
        foreach (string folder in handler.FolderPaths)
        {
            if (!Directory.Exists(folder))
            {
                Logs.Debug($"ResolveMissingLora skipping missing folder '{folder}'");
                continue;
            }
            Logs.Debug($"ResolveMissingLora scanning folder '{folder}' recursively for leaf '{targetLeaf}'");
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (!isSupportedModelFile(file) || NormalizeModelLeaf(file) != targetLeaf)
                {
                    continue;
                }
                diskMatches.Add(Path.GetFullPath(file));
                if (diskMatches.Count > 1)
                {
                    break;
                }
            }
            if (diskMatches.Count > 1)
            {
                break;
            }
        }
        if (diskMatches.Count == 1)
        {
            string fullPath = diskMatches[0];
            Logs.Debug($"ResolveMissingLora disk filename match found: '{targetLeaf}' -> '{fullPath}'");
            using (ManyReadOneWriteLock.WriteClaim writeClaim = Program.RefreshLock.LockWrite())
            {
                handler.Refresh();
            }
            using ManyReadOneWriteLock.ReadClaim readClaim = Program.RefreshLock.LockRead();
            T2IModel refreshed = handler.Models.Values.FirstOrDefault(m => session.User.IsAllowedModel(m.Name)
                && string.Equals(Path.GetFullPath(m.RawFilePath), fullPath, StringComparison.OrdinalIgnoreCase));
            if (refreshed is not null)
            {
                Logs.Debug($"ResolveMissingLora refreshed match found: '{fullPath}' -> '{refreshed.Name}'");
                return LoraResolveResult(refreshed, "disk-filename");
            }
            Logs.Debug($"ResolveMissingLora disk match path was found but handler refresh did not surface it: '{fullPath}'");
        }
        else if (diskMatches.Count > 1)
        {
            Logs.Debug($"ResolveMissingLora disk filename search ambiguous for '{targetLeaf}': {string.Join(", ", diskMatches.Take(10))}");
            return new JObject() { ["error"] = "Multiple LoRAs on disk matched that filename; unable to auto-fix safely." };
        }
        Logs.Debug($"ResolveMissingLora failed: no match found for input='{loraName}', normalized='{normalized}', leaf='{targetLeaf}'");
        return new JObject() { ["error"] = "Unable to find a matching LoRA automatically." };
    }
}