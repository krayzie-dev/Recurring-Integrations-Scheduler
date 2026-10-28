using Microsoft.Extensions.Localization;
using MudBlazor;
using RIS.Core.Configuration;
using RIS.Core.Options;
using RIS.Persistence;
using RIS.Server.Resources;

namespace RIS.Server.Components.Shared;

/// <summary>Opens the app registration dialog (new or existing) from the "App registrations" page and from the instance dialog.</summary>
public static class AppRegistrationEditor
{
    /// <summary>Name of the saved app registration, or null when the dialog was cancelled.</summary>
    public static async Task<string?> OpenAsync(IDialogService dialogs, ConfigurationRepository repository, IRisConfigurationStore store,
        IStringLocalizer<UiText> text, AppRegistrationOptions? existing, string? user)
    {
        var item = existing is null
            ? new AppRegistrationOptions { Name = "", Tenant = "", ClientId = "" }
            : ConfigurationRepository.WithoutSecrets(existing);
        string? saved = null;
#pragma warning disable CS0618
        var dialog = await dialogs.ShowAsync<AppRegistrationDialog>(text["Nav_AppRegistrations"], new DialogParameters<AppRegistrationDialog>
        {
            { d => d.Title, existing is null ? text["AppReg_Add"] : existing.Name },
            { d => d.Item, item },
            { d => d.IsNew, existing is null },
            { d => d.StoredClientSecret, existing is not null && !SecretPlaceholder.IsPlaceholderOrEmpty(store.Current.FindAppRegistration(existing.Name)?.ClientSecret) },
            { d => d.StoredUserPassword, existing is not null && !SecretPlaceholder.IsPlaceholderOrEmpty(store.Current.FindAppRegistration(existing.Name)?.UserPassword) },
            { d => d.Save, async registration =>
                {
                    if (existing is not null && !string.Equals(existing.Name, registration.Name, StringComparison.OrdinalIgnoreCase)) return [text["Reference_NoRename"]];
                    try
                    {
                        await repository.SaveAppRegistrationAsync(registration, user);
                        saved = registration.Name;
                        return [];
                    }
                    catch (ConfigurationValidationException ex)
                    {
                        return ex.Errors;
                    }
                }
            }
        }, new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
#pragma warning restore CS0618
        return await dialog.Result is { Canceled: false } ? saved : null;
    }
}
