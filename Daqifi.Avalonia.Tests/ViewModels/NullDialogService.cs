using Avalonia.Controls;
using Daqifi.Desktop.DialogService;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// An <see cref="IDialogService"/> that shows nothing and confirms everything — the dependency
/// <c>DaqifiViewModel</c>'s constructor requires but that a view-model test never wants to exercise.
/// </summary>
/// <remarks>
/// Shared by every suite that stands a <c>DaqifiViewModel</c> up. It began as a private nested class
/// in <see cref="DeviceRefusalCrashTests"/> and was lifted out when the second suite needed it,
/// rather than copied — two stubs of one interface drift, and a dialog stub that silently stops
/// answering <c>true</c> changes what the code under test does without failing on its own.
/// </remarks>
internal sealed class NullDialogService : IDialogService
{
    public void Register(Control view) { }

    public void Unregister(Control view) { }

    public Task<bool?> ShowDialogAsync<T>(object ownerViewModel, object viewModel) where T : Window
        => Task.FromResult<bool?>(true);
}
