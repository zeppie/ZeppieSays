using System.Windows;
using System.Windows.Interop;
using ZeppieSays.Core;
using ZeppieSays.Services;

namespace ZeppieSays.UI;

/// <summary>The Microsoft To Do card in Settings: the app registration, and signing in.</summary>
public sealed class TodoViewModel : ObservableObject
{
    readonly AppSettings _settings;
    readonly TodoClient _todo;
    readonly Action _save;

    public RelayCommand SignIn { get; }
    public RelayCommand SignOut { get; }

    public TodoViewModel(AppSettings settings, TodoClient todo, Action save)
    {
        _settings = settings;
        _todo = todo;
        _save = save;
        SignIn = new RelayCommand(_ => _ = SignInAsync());
        SignOut = new RelayCommand(_ => _ = SignOutAsync());
        _ = RefreshAsync();
    }

    public string ClientId
    {
        get => _settings.Todo.ClientId;
        set { _settings.Todo.ClientId = value?.Trim() ?? ""; _save(); Raise(); _ = RefreshAsync(); }
    }

    public string Tenant
    {
        get => _settings.Todo.Tenant;
        set { _settings.Todo.Tenant = string.IsNullOrWhiteSpace(value) ? "organizations" : value.Trim(); _save(); Raise(); _ = RefreshAsync(); }
    }

    public string? SignedInAs
    {
        get => _signedInAs;
        private set { if (Set(ref _signedInAs, value)) { Raise(nameof(IsSignedIn)); Raise(nameof(CanSignIn)); } }
    }
    string? _signedInAs;

    public bool IsSignedIn => SignedInAs is not null;
    public bool CanSignIn => SignedInAs is null && _todo.IsConfigured && !_busy;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }
    string _status = "";

    bool _busy;

    async Task RefreshAsync()
    {
        SignedInAs = await _todo.SignedInAsAsync();
        Status = !_todo.IsConfigured ? "Fill in the client ID of your app registration first (see below)."
            : SignedInAs is null ? "Not signed in yet."
            : await ListsTextAsync();
        Raise(nameof(CanSignIn));
    }

    async Task<string> ListsTextAsync()
    {
        try
        {
            var lists = await _todo.ListsAsync(CancellationToken.None);
            return $"Signed in as {SignedInAs}. Lists: {string.Join(", ", lists.Select(l => l.Name))}.";
        }
        catch (Exception e) when (e is TodoException or System.Net.Http.HttpRequestException)
        {
            return e.Message;
        }
    }

    async Task SignInAsync()
    {
        _busy = true;
        Raise(nameof(CanSignIn));
        Status = "Sign in in the browser window that opened…";
        try
        {
            var owner = Application.Current.MainWindow is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
            await _todo.SignInAsync(owner);
        }
        catch (TodoException e)
        {
            Status = e.Message;
            return;
        }
        catch (Microsoft.Identity.Client.MsalException e) // cancelled in the browser
        {
            Log.Info($"Microsoft sign-in stopped: {e.ErrorCode}");
        }
        finally
        {
            _busy = false;
        }
        await RefreshAsync();
    }

    async Task SignOutAsync()
    {
        await _todo.SignOutAsync();
        await RefreshAsync();
    }
}
