using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using RainWorldCompanion.Controls;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.App.Tests;

public class MapArtworkTests
{
    public static IEnumerable<object[]> Maps => DenMapCatalog.Maps.Select(map => new object[] { map.Id });

    [Fact]
    public void Canvas_renders_artwork_and_clears_it_when_exploration_changes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var output = Environment.GetEnvironmentVariable("RWCOMPANION_ARTWORK_PROOF");
                if (output is not null) Directory.CreateDirectory(output);
                foreach (var map in DenMapCatalog.Maps)
                {
                    var canvas = new LiveMapCanvas();
                    canvas.Load(map);
                    canvas.Measure(new Size(map.ImageWidth, map.ImageHeight));
                    canvas.Arrange(new Rect(0, 0, map.ImageWidth, map.ImageHeight));
                    var room = RoomMapCatalog.ForMap(map.Id).First(r => r.RegionCode == "SU" && !r.RoomId.StartsWith("GATE_"));
                    canvas.SetExploration(true, [room.RoomId]);
                    var heading = MapArtworkCatalog.ForMap(map.Id).Single(feature => feature.Region == "SU");
                    var pixel = heading.Rectangles[0];
                    var partial = Render(canvas, map);
                    Assert.NotEqual(0, ColorAt(partial, pixel[0], pixel[1]));
                    if (output is not null) Save(partial, Path.Combine(output, map.Id + "-one-room.png"));
                    canvas.SetExploration(true, []);
                    Assert.Equal(0, ColorAt(Render(canvas, map), pixel[0], pixel[1]));
                    if (output is not null)
                    {
                        canvas.SetExploration(true, RoomMapCatalog.ForMap(map.Id).Select(r => r.RoomId));
                        Save(Render(canvas, map), Path.Combine(output, map.Id + "-all-visited.png"));
                        canvas.SetExploration(true, ["SU_B13"]);
                        Save(Render(canvas, map), Path.Combine(output, map.Id + "-gate-approach.png"));
                        if (map.Id == "Downpour")
                        {
                            foreach (var approach in new[] { "UW_PREGATE", "SL_BRIDGEEND" })
                            {
                                canvas.SetExploration(true, [approach]);
                                Save(Render(canvas, map), Path.Combine(output, map.Id + "-" + approach + ".png"));
                            }
                        }
                    }
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private static RenderTargetBitmap Render(LiveMapCanvas canvas, DenMapDefinition map)
    {
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(map.ImageWidth, map.ImageHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        return bitmap;
    }

    private static int ColorAt(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[0] | pixel[1] | pixel[2];
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void Each_timeline_has_region_headings_and_independent_artwork(string mapId)
    {
        var features = MapArtworkCatalog.ForMap(mapId);
        var expectedRegions = RoomMapCatalog.ForMap(mapId).Select(room => room.RegionCode).Distinct().Order().ToArray();
        Assert.Equal(expectedRegions, features.Where(feature => feature.Kind == "region").Select(feature => feature.Region).Order().ToArray());
        foreach (var kind in new[] { "gate", "pipe", "label" })
            Assert.Contains(features, feature => feature.Kind == kind);
        Assert.True(LiveMapCanvas.CreateRevealForMap(mapId, Visits()).IsEmpty());
    }

    [Fact]
    public void A_region_heading_reveals_from_an_unmapped_room_without_revealing_other_regions()
    {
        var features = MapArtworkCatalog.ForMap("Downpour");
        var visits = Visits("su_unmapped");
        var mask = LiveMapCanvas.CreateRevealForMap("Downpour", visits);
        foreach (var feature in features.Where(feature => feature.Kind == "region"))
        {
            var point = Pixel(feature.Rectangles[0]);
            Assert.Equal(feature.Region == "SU", mask.FillContains(point));
        }
    }

    [Fact]
    public void Gate_artwork_reveals_from_either_approach_without_revealing_the_other_room()
    {
        var gate = MapArtworkCatalog.ForMap("Downpour").Single(feature => feature.Kind == "gate" && feature.Text == "GATE_SU_HI");
        foreach (var approach in new[] { "SU_B13", "HI_B04", "GATE_SU_HI" })
        {
            var mask = LiveMapCanvas.CreateRevealForMap("Downpour", Visits(approach));
            foreach (var rect in gate.Rectangles) Assert.True(mask.FillContains(Pixel(rect)));
            var other = RoomMapCatalog.Find("Downpour", approach == "HI_B04" ? "SU_B13" : "HI_B04")!;
            Assert.False(mask.FillContains(new Point(other.X, other.Y)));
        }
    }

    [Fact]
    public void Pipe_artwork_does_not_reveal_unvisited_room_actions()
    {
        var view = new RainWorldCompanion.ViewModels.LiveMapViewModel();
        view.Adopt(new() { SessionId = "session", Campaign = "White", Timeline = "White", EnabledExpansions = ["moreslugcats"],
            HasExplorationData = true, VisitedRooms = ["SU_A37"] });
        Assert.False(view.IsRoomVisible("SU_S04"));
        view.Query = "SU_S04";
        Assert.Empty(view.SearchResults);
        var pipe = MapArtworkCatalog.ForMap("Downpour").Single(feature => feature.Kind == "pipe"
            && feature.Rooms.SequenceEqual(new[] { "SU_A37", "SU_S04" }));
        Assert.True(LiveMapCanvas.CreateRevealForMap("Downpour", Visits("SU_A37")).FillContains(Pixel(pipe.Rectangles[0])));
    }

    [Theory]
    [InlineData("Downpour")]
    [InlineData("Rivulet")]
    public void Saint_only_room_artwork_stays_hidden_when_its_approach_is_visited(string mapId)
    {
        var room = MapArtworkCatalog.ForMap(mapId).Single(feature => feature.Kind == "room" && feature.Rooms.Contains("MS_ARTERY12"));
        var pixel = Pixel(room.Rectangles[0]);
        Assert.False(LiveMapCanvas.CreateRevealForMap(mapId, Visits("MS_MEM06")).FillContains(pixel));
        Assert.True(LiveMapCanvas.CreateRevealForMap(mapId, Visits("MS_ARTERY12")).FillContains(pixel));
    }

    [Fact]
    public void Rubicon_transition_reveals_from_either_end()
    {
        var connection = MapArtworkCatalog.ForMap("Saint").Single(feature => feature.Kind == "pipe"
            && feature.Text == "SB_D06_HR_C01");
        foreach (var name in new[] { "SB_D06", "HR_C01" })
            Assert.True(LiveMapCanvas.CreateRevealForMap("Saint", Visits(name)).FillContains(Pixel(connection.Rectangles[0])));
    }

    [Theory]
    [InlineData("Artificer")]
    [InlineData("Spearmaster")]
    [InlineData("Rivulet")]
    public void Gourmand_only_room_artwork_stays_hidden_when_a_nearby_room_is_visited(string mapId)
    {
        var rooms = MapArtworkCatalog.ForMap(mapId).Where(feature => feature.Kind == "room"
            && feature.Rooms.Any(name => name is "SH_GOR01" or "SH_GOR02")).ToArray();
        Assert.Equal(2, rooms.Length);
        var mask = LiveMapCanvas.CreateRevealForMap(mapId, Visits("SH_HELPOUT", "SH_A22", "SH_B05"));
        foreach (var room in rooms)
        {
            Assert.False(mask.FillContains(Pixel(room.Rectangles[0])));
            Assert.True(LiveMapCanvas.CreateRevealForMap(mapId, Visits(room.Rooms)).FillContains(Pixel(room.Rectangles[0])));
        }
    }

    [Fact]
    public void Interrupted_shoreline_pipe_reveals_from_either_connected_room()
    {
        var pipe = MapArtworkCatalog.ForMap("Saint").Single(feature => feature.Kind == "pipe"
            && feature.Rooms.SequenceEqual(new[] { "SL_C02", "SL_C10" }));
        Assert.Contains(pipe.Rectangles, rect => rect[1] > 2600 && rect[1] < 2750);
        Assert.Contains(pipe.Rectangles, rect => rect[1] > 2800);
        foreach (var room in pipe.Rooms)
        {
            var mask = LiveMapCanvas.CreateRevealForMap("Saint", Visits(room));
            foreach (var rect in pipe.Rectangles) Assert.True(mask.FillContains(Pixel(rect)));
        }
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void Every_gate_reveals_from_each_approach_and_stays_hidden_without_visits(string mapId)
    {
        foreach (var gate in MapArtworkCatalog.ForMap(mapId).Where(feature => feature.Kind == "gate"))
        {
            Assert.False(gate.IsVisible(Visits(), Visits()));
            foreach (var approach in gate.Rooms)
            {
                var mask = LiveMapCanvas.CreateRevealForMap(mapId, Visits(approach));
                foreach (var rect in gate.Rectangles.Where((_, i) => i % 37 == 0))
                    Assert.True(mask.FillContains(Pixel(rect)), $"{mapId} {gate.Text} from {approach}");
            }
        }
    }

    [Fact]
    public void Precipice_label_does_not_interrupt_the_gate_line_from_the_exterior()
    {
        var mask = LiveMapCanvas.CreateRevealForMap("Downpour", Visits("UW_PREGATE"));
        for (int x = 8176; x <= 8400; x++)
            Assert.True(mask.FillContains(new Point(x + 0.5, 2794.5)));
    }

    private static HashSet<string> Visits(params string[] rooms) => new(rooms, StringComparer.OrdinalIgnoreCase);
    private static Point Pixel(int[] rect) => new(rect[0] + 0.5, rect[1] + 0.5);
}
