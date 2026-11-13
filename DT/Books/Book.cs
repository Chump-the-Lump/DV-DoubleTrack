using System.Reflection;
using Newtonsoft.Json;

namespace DoubleTrack.Books;

/// <summary>
/// Double Track's switches in the game's two switch books: the route map (RouteMapBook) and the
/// station booklet (StationBooklet). Each book is drawn for the Normal layout in a drawing kept
/// in this assembly (Books/*.json), in the books' own tiles. A book is changed only when every
/// Double Track switch its drawing shows is found where the drawing has it; otherwise it stays
/// as the game made it, so a touch never throws the wrong switch.
/// </summary>
internal static class Book
{
    /// <summary>Where a book page's contents sit in its render prefab.</summary>
    internal const string WrapperPath = "Template Canvas/BackgroundImage/OuterWrapper";

    /// <summary>Names the tiles and badges these patches add.</summary>
    internal const string OwnPrefix = "doubletrack ";

    internal static T? LoadDrawing<T>(string name) where T : class
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DoubleTrack.Books." + name);
        if (stream == null)
        {
            Warn($"no drawing {name}");
            return null;
        }
        using var reader = new StreamReader(stream);
        return JsonConvert.DeserializeObject<T>(reader.ReadToEnd());
    }

    internal static void Log(string text) => TrackPlacerEntry.ModEntry?.Logger.Log(text);
    internal static void Warn(string text) => TrackPlacerEntry.ModEntry?.Logger.Warning(text);
    internal static void Error(string text) => TrackPlacerEntry.ModEntry?.Logger.Error(text);
}
