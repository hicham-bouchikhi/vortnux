using Vortnux.Core;

namespace Vortnux.Desktop;

public sealed partial class MainWindow
{
    internal Task ResumeAfterUpdateAsync() => credentials is null ? Task.CompletedTask : RestoreAccountAsync(true);

    private async Task RestoreAccountAsync(bool browse)
    {
        await Work(async () =>
        {
            var key = await credentials!.LoadAsync(credentialLifetime.Token);
            if (key is null) { status.Text = "Enter your Nexus API key in Settings to connect."; return; }
            try
            {
                var api = new NexusApi(http, key);
                var user = await api.ValidateAsync(credentialLifetime.Token);
                if (closed) return;
                SetAccount(api, user);
                if (browse) page = "Browse";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // Validation errors must never include a stored credential.
                status.Text = "Saved Nexus key could not connect. Check your connection or replace/forget the key in Settings.";
            }
        });
    }

    private void SetAccount(NexusApi api, NexusAccount user)
    {
        catalogSession?.Dispose(); nexus = api; account = user;
        catalogImages.Clear();
        catalogSession = new CatalogSession(new NexusCatalog(api));
        catalogSession.Changed += () => { if (page == "Browse") Render(); };
        status.Text = "Connected as " + user.Name;
    }

    private void DisconnectAccount()
    {
        catalogSession?.Dispose(); catalogSession = null; nexus = null; account = null;
        catalogImages.Clear();
        resolvedMod = null; resolvedFiles = []; collection = null; collectionPlan = null;
        selectedCatalogItem = null; linkText = "";
        status.Text = "Disconnected";
    }
}
