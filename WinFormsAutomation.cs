using System.Reflection;
using System.Runtime.InteropServices;

namespace XMLator;

internal sealed class OptionCheckBox : CheckBox
{
    private const int ToggleStatePropertyId = 30086;

    protected override void OnCheckedChanged(EventArgs e)
    {
        bool isChecked = Checked;
        base.OnCheckedChanged(e);
        if (IsHandleCreated && Checked == isChecked)
            WinFormsAutomation.RaisePropertyChange(AccessibilityObject, ToggleStatePropertyId, isChecked ? 0 : 1, isChecked ? 1 : 0);
    }
}

internal static class WinFormsAutomation
{
    private const BindingFlags Operators = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    // WinForms issue dotnet/winforms#15201 tracks the missing state notifications in .NET 10.
    // Use the built-in provider from the bundled runtime so the event and the accessible control share an identity.
    private static readonly MethodInfo? Raise = typeof(AccessibleObject).GetMethod("RaiseAutomationPropertyChangedEvent",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly ParameterInfo[]? Arguments = Raise?.GetParameters();
    private static readonly Dictionary<Type, MethodInfo?> ConvertValue = new[] { typeof(int), typeof(string) }
        .ToDictionary(type => type, type => Arguments is { Length: 3 } ? Arguments[1].ParameterType.GetMethod("op_Explicit", Operators, [type]) : null);

    internal static void RaisePropertyChange(AccessibleObject provider, int propertyId, object previous, object current)
    {
        if (Arguments is not { Length: 3 } || !ConvertValue.TryGetValue(previous.GetType(), out var convert) ||
            convert is null || !UiaClientsAreListening())
            return;
        var id = Enum.ToObject(Arguments[0].ParameterType, propertyId);
        var oldValue = convert.Invoke(null, [previous]);
        var newValue = convert.Invoke(null, [current]);
        try { Raise!.Invoke(provider, [id, oldValue, newValue]); }
        finally
        {
            (oldValue as IDisposable)?.Dispose();
            (newValue as IDisposable)?.Dispose();
        }
    }

    [DllImport("uiautomationcore.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiaClientsAreListening();
}
