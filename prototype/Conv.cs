using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Vcm.Prototype;

public static class Conv
{
    /// <summary>Root folders are bolded in the tree; their children are not.</summary>
    public static readonly IValueConverter BoolToWeight = new FuncValueConverter<bool, FontWeight>(
        isRoot => isRoot ? FontWeight.SemiBold : FontWeight.Normal);
}

internal sealed class FuncValueConverter<TIn, TOut> : IValueConverter
{
    private readonly Func<TIn, TOut> _fn;
    public FuncValueConverter(Func<TIn, TOut> fn) => _fn = fn;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TIn t ? _fn(t) : default(TOut);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
