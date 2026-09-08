using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace FantasyMontior;

public sealed class WidgetScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (double)value / 16;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

// Only our packaged, path-only SVGs are supported. No external SVG documents are accepted.
public sealed class WidgetIconConverter : IValueConverter
{
    private static readonly Dictionary<string, DrawingImage> Cache = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var name = value as string ?? "cpu";
        if (name is not ("cpu" or "gpu" or "ram")) name = "cpu";
        if (Cache.TryGetValue(name, out var image)) return image;
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/FantasyMontior;component/Resources/Icons/{name}.svg"));
        using var stream = resource.Stream;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var svg = XDocument.Load(reader).Root!;
        if ((string?)svg.Attribute("viewBox") != "0 0 24 24") throw new FormatException("Icon must use the shared 24px viewBox.");
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        foreach (var path in svg.Elements())
        {
            if (path.Name.LocalName != "path") throw new FormatException("Only SVG paths are supported.");
            var geometry = Geometry.Parse((string)path.Attribute("d")!);
            var pen = new Pen((Brush)new BrushConverter().ConvertFromInvariantString((string)path.Attribute("stroke")!)!,
                double.Parse((string)path.Attribute("stroke-width")!, CultureInfo.InvariantCulture));
            drawing.Children.Add(new GeometryDrawing(null, pen, geometry));
        }
        image = new DrawingImage(drawing);
        image.Freeze();
        Cache[name] = image;
        return image;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
