using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DotValet.ViewModels;
using DotValet.Views;

namespace DotValet;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm };
            vm.Ui = window;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => Task.Run(vm.ShutdownAsync).Wait(TimeSpan.FromSeconds(5));
            _ = vm.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
