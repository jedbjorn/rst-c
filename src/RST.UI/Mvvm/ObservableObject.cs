// Copied from splash-it src/SplashIt.UI/Mvvm/ObservableObject.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Mvvm/ObservableObject.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*).

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RST.UI.Mvvm;

/// <summary>Plain <see cref="INotifyPropertyChanged"/> base for the view-models (gantt-it §8: no MVVM packages).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
