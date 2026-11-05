using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Status;

namespace PS5LM.Companion.Core.Models;

/// <summary>The app's "load a model at launch" setting, as stored in <c>settings.json</c>.</summary>
/// <param name="AutoLoad">When true, the next launch loads <see cref="DefaultModel"/>.</param>
/// <param name="DefaultModel">The model path as the app sees it, or null when none is chosen.</param>
/// <param name="DefaultModelInLibrary">False when <see cref="DefaultModel"/> is set and not a model on the console.</param>
public sealed record AutoLoadSettings(bool AutoLoad, string? DefaultModel, bool DefaultModelInLibrary);

/// <summary>The outcome of reading <c>settings.json</c>. <see cref="Settings"/> is set only when the file could be read.</summary>
public sealed record AutoLoadRead(OperationOutcome Outcome, string? Message = null, AutoLoadSettings? Settings = null)
{
    public bool Succeeded => Settings is not null;
}

/// <summary>
/// Reads and writes <c>auto_load</c> and <c>default_model</c> in the app's <c>settings.json</c>.
/// Every other key is kept. The app reads the file when it starts and rewrites it from memory when settings are
/// saved on the console, so a change here applies at the next launch and can be overwritten before then.
/// </summary>
public sealed class AutoLoadSettingsService(
    IPS5FileSystem fileSystem,
    ModelCatalog catalog,
    CompanionStatusService statusService,
    CompanionStatusStore store)
{
    /// <summary>The largest settings file that is read. A bigger file is left untouched.</summary>
    public const int MaxSettingsBytes = 64 * 1024;

    private const string AutoLoadKey = "auto_load";

    private const string DefaultModelKey = "default_model";

    private const string Unreadable = "settings.json could not be read, so it was left unchanged.";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // A read of the file and the write of its replacement must not interleave.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AutoLoadRead> GetAsync(CancellationToken cancellationToken)
    {
        var installation = await InstallationAsync(cancellationToken);
        if (installation?.StateRoot is not { } root)
        {
            return Unavailable(installation);
        }

        var document = await ReadAsync(root, cancellationToken);
        if (document.Error is not null || !document.TypesOk || document.Object is null)
        {
            return new AutoLoadRead(OperationOutcome.Invalid, document.Error ?? Unreadable);
        }

        var models = await catalog.ListAsync(installation, store.Current.LoadedModel, cancellationToken);
        return new AutoLoadRead(OperationOutcome.Completed, Settings: ToSettings(document, models));
    }

    public async Task<OperationResult> SetAsync(bool autoLoad, string? defaultModel, CancellationToken cancellationToken)
    {
        var requested = string.IsNullOrEmpty(defaultModel) ? null : defaultModel;
        if (autoLoad && requested is null)
        {
            return new OperationResult(OperationOutcome.Invalid, "Choose a model before turning auto-load on.");
        }

        var installation = await InstallationAsync(cancellationToken);
        if (installation?.StateRoot is not { } root)
        {
            return new OperationResult(OperationOutcome.Unavailable, installation?.Message ?? "The console is not reachable.");
        }

        if (requested is not null)
        {
            var models = await catalog.ListAsync(installation, store.Current.LoadedModel, cancellationToken);
            var model = models.FirstOrDefault(m => m.AppPath.Equals(requested, StringComparison.Ordinal));
            if (model is null)
            {
                return new OperationResult(OperationOutcome.NotFound, "That model is not on the console.");
            }

            if (model.IsProjector)
            {
                return new OperationResult(OperationOutcome.NotFound, "A vision projector cannot load at launch.");
            }
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadAsync(root, cancellationToken);
            if (document.Error is not null || document.Object is null)
            {
                return new OperationResult(OperationOutcome.Invalid, document.Error ?? Unreadable);
            }

            // A wrong type is a change: the write replaces it with a bool and a string. An unreadable file is not.
            if (document.TypesOk && document.AutoLoad == autoLoad && string.Equals(document.DefaultModel, requested, StringComparison.Ordinal))
            {
                return new OperationResult(OperationOutcome.NoChange, "Already set that way.");
            }

            document.Object[AutoLoadKey] = autoLoad;
            document.Object[DefaultModelKey] = requested ?? string.Empty;
            var content = document.Object.ToJsonString(Json) + "\n";
            var path = SettingsPath(root);
            var temporary = path + ".tmp";
            await fileSystem.WriteTextAsync(temporary, content, cancellationToken);
            await fileSystem.MoveAsync(temporary, path, cancellationToken);

            var written = await fileSystem.ReadTextAsync(path, Encoding.UTF8.GetByteCount(content), cancellationToken);
            if (!string.Equals(written, content, StringComparison.Ordinal))
            {
                return new OperationResult(OperationOutcome.Unavailable, "The setting was written but did not read back.");
            }

            return new OperationResult(OperationOutcome.Completed, "Applies the next time the app starts.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Installation?> InstallationAsync(CancellationToken cancellationToken)
    {
        if (statusService.LastInstallation is null)
        {
            await statusService.RefreshAsync(cancellationToken);
        }

        return statusService.LastInstallation;
    }

    private async Task<SettingsDocument> ReadAsync(StorageRoot root, CancellationToken cancellationToken)
    {
        var path = SettingsPath(root);
        var text = await fileSystem.ReadTextAsync(path, MaxSettingsBytes, cancellationToken);
        if (text is null)
        {
            // Missing is the app's own default. Present but unreadable must not be overwritten.
            return await fileSystem.FileExistsAsync(path, cancellationToken)
                ? SettingsDocument.Failed(Unreadable)
                : SettingsDocument.Missing();
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return SettingsDocument.Failed(Unreadable);
        }

        if (node is not JsonObject obj)
        {
            return SettingsDocument.Failed(Unreadable);
        }

        var autoLoadOk = TryBool(obj, AutoLoadKey, out var autoLoad);
        var modelOk = TryString(obj, DefaultModelKey, out var model);
        return new SettingsDocument(obj, autoLoad, model, autoLoadOk && modelOk, null);
    }

    private static AutoLoadSettings ToSettings(SettingsDocument document, IReadOnlyList<ModelFile> models)
    {
        var inLibrary = document.DefaultModel is null
            || models.Any(m => !m.IsProjector && m.AppPath.Equals(document.DefaultModel, StringComparison.Ordinal));
        return new AutoLoadSettings(document.AutoLoad, document.DefaultModel, inLibrary);
    }

    /// <summary>A missing key keeps the app's default. A present key of the wrong type, including null, does not.</summary>
    private static bool TryBool(JsonObject obj, string name, out bool value)
    {
        if (!obj.TryGetPropertyValue(name, out var node))
        {
            value = false;
            return true;
        }

        if (node is JsonValue json && json.TryGetValue<bool>(out value))
        {
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryString(JsonObject obj, string name, out string? value)
    {
        if (!obj.TryGetPropertyValue(name, out var node))
        {
            value = null;
            return true;
        }

        if (node is JsonValue json && json.TryGetValue<string>(out var text))
        {
            value = string.IsNullOrEmpty(text) ? null : text;
            return true;
        }

        value = null;
        return false;
    }

    private static string SettingsPath(StorageRoot root) => $"{root.FtpPath}/{VariantProfile.SettingsFile}";

    private static AutoLoadRead Unavailable(Installation? installation) =>
        new(OperationOutcome.Unavailable, installation?.Message ?? "The console is not reachable.");

    private sealed record SettingsDocument(JsonObject? Object, bool AutoLoad, string? DefaultModel, bool TypesOk, string? Error)
    {
        public static SettingsDocument Missing() => new(new JsonObject(), false, null, true, null);

        public static SettingsDocument Failed(string error) => new(null, false, null, false, error);
    }
}
