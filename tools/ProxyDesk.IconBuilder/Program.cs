using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

if (args.Length != 1) throw new ArgumentException("Provide the desktop asset directory.");
var directory = Path.GetFullPath(args[0]);
var source = XDocument.Load(Path.Combine(directory, "ProxyDesk.svg")).Root!;
float Number(XElement element, string name) => float.Parse(element.Attribute(name)!.Value, CultureInfo.InvariantCulture);
byte[] Render(int size)
{
    using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent); canvas.Scale(size / 256f);
    foreach (var element in source.Elements())
    {
        using var paint = new SKPaint { IsAntialias = true };
        if (element.Name.LocalName == "rect")
        {
            paint.Color = SKColor.Parse(element.Attribute("fill")!.Value);
            canvas.DrawRoundRect(new SKRect(Number(element, "x"), Number(element, "y"), Number(element, "x") + Number(element, "width"),
                Number(element, "y") + Number(element, "height")), Number(element, "rx"), Number(element, "rx"), paint);
        }
        else if (element.Name.LocalName == "path")
        {
            paint.Style = SKPaintStyle.Stroke; paint.Color = SKColor.Parse(element.Attribute("stroke")!.Value);
            paint.StrokeWidth = Number(element, "stroke-width"); paint.StrokeCap = SKStrokeCap.Round; paint.StrokeJoin = SKStrokeJoin.Round;
            using var path = SKPath.ParseSvgPathData(element.Attribute("d")!.Value); canvas.DrawPath(path, paint);
        }
        else throw new InvalidDataException("Unsupported icon SVG element: " + element.Name.LocalName);
    }
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
}

File.WriteAllBytes(Path.Combine(directory, "ProxyDesk.png"), Render(256));
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var frames = sizes.Select(Render).ToArray();
using var output = File.Create(Path.Combine(directory, "ProxyDesk.ico"));
using var writer = new BinaryWriter(output);
writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
var offset = 6 + 16 * sizes.Length;
for (var index = 0; index < sizes.Length; index++)
{
    writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index])); writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
    writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
    writer.Write(frames[index].Length); writer.Write(offset); offset += frames[index].Length;
}
foreach (var frame in frames) writer.Write(frame);
Console.WriteLine("ICON_BUILD_PASS: 9 resolutions, 16-256 pixels");
