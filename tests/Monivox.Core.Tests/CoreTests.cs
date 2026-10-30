using Monivox.Core;

namespace Monivox.Core.Tests;

public class LayoutResolverTests
{
    [Fact]
    public void HalfVertical_On4K_GivesTwoEqualHalves()
    {
        var specs = LayoutResolver.Resolve(LayoutPresets.HalfVertical, new DisplayMode(3840, 2160, 144));

        Assert.Equal(2, specs.Count);
        Assert.Equal(new PixelRect(0, 0, 1920, 2160), specs[0].Dest);
        Assert.Equal(new PixelRect(1920, 0, 1920, 2160), specs[1].Dest);
        Assert.Equal(new DisplayMode(1920, 2160, 144), specs[0].Mode);
    }

    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(3440, 1440)]
    [InlineData(5120, 1440)]
    [InlineData(1366, 768)]
    public void AllPresets_TileWithoutGapsOrOverlap(int w, int h)
    {
        foreach (var (name, layout) in LayoutPresets.All)
        {
            var specs = LayoutResolver.Resolve(layout, new DisplayMode(w, h, 60));
            long area = specs.Sum(s => (long)s.Dest.Width * s.Dest.Height);
            Assert.True(area == (long)w * h, $"{name}: Fläche {area} statt {(long)w * h}");

            for (int i = 0; i < specs.Count; i++)
            {
                var a = specs[i].Dest;
                Assert.True(a.X >= 0 && a.Y >= 0 && a.Right <= w && a.Bottom <= h, $"{name}: Bereich {i} außerhalb");
                for (int j = i + 1; j < specs.Count; j++)
                {
                    var b = specs[j].Dest;
                    bool overlap = a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
                    Assert.False(overlap, $"{name}: Bereich {i} überlappt {j}");
                }
            }
        }
    }

    [Fact]
    public void ThreeColumns_On2560_RoundsEdgesNotWidths()
    {
        var specs = LayoutResolver.Resolve(LayoutPresets.ThreeColumns, new DisplayMode(2560, 1440, 60));
        Assert.Equal(new[] { 853, 854, 853 }, specs.Select(s => s.Dest.Width).ToArray());
        Assert.Equal(specs[0].Dest.Right, specs[1].Dest.X);
        Assert.Equal(specs[1].Dest.Right, specs[2].Dest.X);
    }
}

public class LayoutValidationTests
{
    [Fact]
    public void Presets_AreValid()
    {
        foreach (var (name, layout) in LayoutPresets.All)
        {
            Assert.True(layout.Validate().Count == 0, name);
        }
    }

    [Fact]
    public void Overlap_IsDetected()
    {
        var layout = new Layout("x", [new(0, 0, 0.6, 1), new(0.5, 0, 0.5, 1)]);
        Assert.Contains(layout.Validate(), e => e.Contains("überlappt"));
    }

    [Fact]
    public void OutOfBounds_IsDetected()
    {
        var layout = new Layout("x", [new(0.5, 0, 0.6, 1)]);
        Assert.Contains(layout.Validate(), e => e.Contains("außerhalb"));
    }

    [Fact]
    public void TooManyRegions_IsDetected()
    {
        var regions = Enumerable.Range(0, 25).Select(i => new NormRect(i / 25.0, 0, 1 / 25.0, 1));
        Assert.Contains(new Layout("x", regions).Validate(), e => e.Contains("Maximal"));
    }

    [Fact]
    public void TooSmallForResolution_IsDetected()
    {
        var layout = LayoutPresets.Columns("eng", 0.05);
        Assert.Contains(layout.Validate(1920, 1080), e => e.Contains("zu klein"));
    }

    [Fact]
    public void Columns_RejectsUnsortedSplits()
    {
        Assert.Throws<ArgumentException>(() => LayoutPresets.Columns("x", 0.6, 0.3));
    }
}

public class DesktopArrangementTests
{
    [Fact]
    public void SingleMonitor_Primary_GridStaysInPlace_PhysicalParkedRight()
    {
        var physical = new DisplayMode(3840, 2160, 60);
        var specs = LayoutResolver.Resolve(LayoutPresets.Grid2x2, physical);
        var plan = DesktopArrangement.Create(specs, new PixelRect(0, 0, 3840, 2160), physicalIsPrimary: true, []);

        var v = plan.Virtuals;
        Assert.True(v[0].Primary);
        Assert.Equal((0, 0), (v[0].X, v[0].Y));
        Assert.Equal((1920, 0), (v[1].X, v[1].Y));
        Assert.Equal((0, 1080), (v[2].X, v[2].Y));
        Assert.Equal((1920, 1080), (v[3].X, v[3].Y));
        Assert.Equal(new PixelRect(3840, 0, 3840, 2160), plan.ParkedPhysical);
        Assert.Equal(new PixelRect(0, 0, 3840, 2160), plan.CursorArea);
    }

    [Fact]
    public void SecondaryMonitor_IsSplit_OtherMonitorUntouched()
    {
        // Links ein 1080p-Monitor (primär), rechts der 4K-Monitor, der aufgeteilt wird.
        var others = new[] { new DesktopArrangement.OtherDisplay(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), true) };
        var specs = LayoutResolver.Resolve(LayoutPresets.HalfVertical, new DisplayMode(3840, 2160, 60));
        var plan = DesktopArrangement.Create(specs, new PixelRect(1920, 0, 3840, 2160), physicalIsPrimary: false, others);

        Assert.Equal(others[0], Assert.Single(plan.Others));
        Assert.All(plan.Virtuals, v => Assert.False(v.Primary));
        Assert.Equal((1920, 0), (plan.Virtuals[0].X, plan.Virtuals[0].Y));
        Assert.Equal((3840, 0), (plan.Virtuals[1].X, plan.Virtuals[1].Y));
        Assert.Equal(5760, plan.ParkedPhysical.X);
        // Maus erreicht den linken Monitor und beide Hälften, aber nicht den geparkten Monitor
        Assert.Equal(new PixelRect(0, 0, 5760, 2160), plan.CursorArea);
        Assert.True(plan.CursorArea.Right <= plan.ParkedPhysical.X);
    }

    [Fact]
    public void PrimaryMonitorSplit_ShiftsOthersConsistently()
    {
        // Aufzuteilender Monitor primär, ein weiterer Monitor links davon (negative Koordinaten).
        var others = new[] { new DesktopArrangement.OtherDisplay("L", new PixelRect(-2560, 0, 2560, 1440), false) };
        var layout = new Layout("rechts zuerst", [new(0.5, 0, 0.5, 1), new(0, 0, 0.5, 1)]);
        var specs = LayoutResolver.Resolve(layout, new DisplayMode(2560, 1440, 60));
        var plan = DesktopArrangement.Create(specs, new PixelRect(0, 0, 2560, 1440), physicalIsPrimary: true, others);

        // Der Bereich oben links (Index 1) wird primär bei (0,0), alles andere relativ unverändert.
        var primary = Assert.Single(plan.Virtuals, v => v.Primary);
        Assert.Equal(1, primary.Index);
        Assert.Equal((0, 0), (primary.X, primary.Y));
        Assert.Equal((1280, 0), (plan.Virtuals[0].X, plan.Virtuals[0].Y));
        Assert.Equal(new PixelRect(-2560, 0, 2560, 1440), plan.Others[0].Bounds);
        Assert.Equal(2560, plan.ParkedPhysical.X);
    }
}

public class DirectArrangementTests
{
    [Fact]
    public void DirectMode_PhysicalStays_VirtualBlockRightOfEverything()
    {
        // 4K-Monitor rechts neben einem 1080p-Hauptmonitor; Mitte = Direktbereich, links/rechts virtuell
        var layout = new Layout("x", [new(0, 0, 0.25, 1), new(0.25, 0, 0.5, 1), new(0.75, 0, 0.25, 1)]) { DirectIndex = 1 };
        var specs = LayoutResolver.Resolve(layout, new DisplayMode(3840, 2160, 60));
        var virtualSpecs = specs.Where(s => s.Index != layout.DirectIndex).ToList();
        var others = new[] { new DesktopArrangement.OtherDisplay("A", new PixelRect(0, 0, 1920, 1080), true) };
        var plan = DirectArrangement.Create(virtualSpecs, specs[1].Dest, new PixelRect(1920, 0, 3840, 2160), false, others);

        Assert.Equal(new PixelRect(1920, 0, 3840, 2160), plan.Physical);
        Assert.Equal(new PixelRect(1920 + 960, 0, 1920, 2160), plan.WorkArea);
        Assert.Equal(2, plan.Virtuals.Count);
        Assert.All(plan.Virtuals, v => Assert.True(v.X >= 5760));
        // gespiegelt: Bereich 2 liegt 2880 px rechts von Bereich 0
        Assert.Equal(2880, plan.Virtuals[1].X - plan.Virtuals[0].X);
    }

    [Fact]
    public void SmallRegions_AreLaidOutInARow_WithoutOverlap()
    {
        var layout = new Layout("x", [new(0, 0, 1, 0.9), new(0, 0.9, 0.5, 0.1), new(0.5, 0.9, 0.5, 0.1)]) { DirectIndex = 0 };
        var specs = LayoutResolver.Resolve(layout, new DisplayMode(1920, 1080, 60));
        var virtualSpecs = specs.Where(s => s.Index != 0).ToList();
        Assert.False(VirtualBlock.CanMirror(virtualSpecs));
        var plan = DirectArrangement.Create(virtualSpecs, specs[0].Dest, new PixelRect(0, 0, 1920, 1080), true, []);
        var a = plan.Virtuals[0];
        var b = plan.Virtuals[1];
        Assert.Equal(a.X + a.Mode.Width, b.X);
    }
}

public class ConfigFormatTests
{
    [Fact]
    public void VddSettings_SetsCountAndAddsMissingModes_KeepsOthers()
    {
        var doc = System.Xml.Linq.XDocument.Parse("""
            <vdd_settings>
              <monitors><count>1</count></monitors>
              <global><g_refresh_rate>60</g_refresh_rate></global>
              <resolutions><resolution><width>1920</width><height>1080</height><refresh_rate>60</refresh_rate></resolution></resolutions>
              <options><HardwareCursor>false</HardwareCursor></options>
            </vdd_settings>
            """);
        bool changed = VddSettings.Apply(doc, 2, [new DisplayMode(1920, 2160, 144), new DisplayMode(1920, 1080, 60)]);

        Assert.True(changed);
        Assert.Equal(2, VddSettings.GetMonitorCount(doc));
        var res = doc.Root!.Element("resolutions")!.Elements("resolution").ToList();
        Assert.Equal(2, res.Count);
        Assert.Contains(res, r => r.Element("width")!.Value == "1920" && r.Element("height")!.Value == "2160");
        Assert.Contains(doc.Root!.Element("global")!.Elements("g_refresh_rate"), e => e.Value == "144");
        Assert.Equal("true", doc.Root!.Element("options")!.Element("HardwareCursor")!.Value);

        Assert.False(VddSettings.Apply(doc, 2, [new DisplayMode(1920, 2160, 144)]));
    }

    [Fact]
    public void VddSettings_RejectsTooLargeModes()
    {
        var doc = VddSettings.CreateDefault();
        Assert.Throws<ArgumentOutOfRangeException>(() => VddSettings.Apply(doc, 1, [new DisplayMode(8000, 1000, 60)]));
    }

    [Fact]
    public void SmallRegion_GetsUpscaledModeWithSameAspect()
    {
        // 800x200 Telemetrie-Streifen -> virtueller Monitor mind. 640x480, Seitenverhältnis 4:1
        var mode = LayoutResolver.ModeFor(new PixelRect(0, 0, 800, 200), 60);
        Assert.True(mode.Width >= 640 && mode.Height >= 480);
        Assert.Equal(4.0, (double)mode.Width / mode.Height, 2);

        var exact = LayoutResolver.ModeFor(new PixelRect(0, 0, 1920, 2160), 144);
        Assert.Equal(new DisplayMode(1920, 2160, 144), exact);
    }

    [Fact]
    public void CompositorConfig_ContainsViewsDirectAndFrames()
    {
        var cfg = CompositorConfig.Build(@"\\.\DISPLAY1",
        [
            new(@"\\.\DISPLAY3", new PixelRect(0, 0, 1920, 2160), "Discord"),
            new(@"\\.\DISPLAY4", new PixelRect(1920, 0, 960, 2160), "Teams"),
        ], (new PixelRect(2880, 0, 960, 2160), "Film"), frames: true);
        Assert.Contains(@"physical=\\.\DISPLAY1", cfg);
        Assert.Contains(@"view=\\.\DISPLAY3,0,0,1920,2160,Discord", cfg);
        Assert.Contains(@"view=\\.\DISPLAY4,1920,0,960,2160,Teams", cfg);
        Assert.Contains("direct=2880,0,960,2160,Film", cfg);
        Assert.Contains("frames=1", cfg);
    }
}

public class ProfileStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory("sd-test");
        try
        {
            var store = new ProfileStore(Path.Combine(dir.FullName, "settings.json"));
            var profile = new Profile
            {
                Name = "Gaming",
                Layout = LayoutPresets.MainAndTwoStacked,
                Apps =
                [
                    new AppAssignment { Executable = "game.exe", RegionIndex = 0, State = WindowState.BorderlessFullscreen },
                    new AppAssignment { Executable = "discord.exe", RegionIndex = 2, State = WindowState.Normal, Bounds = new PixelRect(10, 10, 800, 600) },
                ],
                Hotkey = "Ctrl+Alt+1",
            };
            store.Save(new SettingsDocument { Profiles = [profile], LastProfile = "Gaming" });

            var loaded = store.Load();
            Assert.Equal("Gaming", loaded.LastProfile);
            var p = Assert.Single(loaded.Profiles);
            Assert.Equal(profile.Layout.Regions, p.Layout.Regions);
            Assert.Equal(WindowState.BorderlessFullscreen, p.Apps[0].State);
            Assert.Equal(new PixelRect(10, 10, 800, 600), p.Apps[1].Bounds);
            Assert.Empty(p.Validate());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void CorruptFile_IsBackedUp_AndDefaultsReturned()
    {
        var dir = Directory.CreateTempSubdirectory("sd-test");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, "{ kaputt");
            var doc = new ProfileStore(path).Load();
            Assert.Empty(doc.Profiles);
            Assert.Single(Directory.GetFiles(dir.FullName, "settings.json.defekt-*"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Profile_WithAppOnMissingRegion_IsInvalid()
    {
        var p = new Profile { Name = "x", Layout = LayoutPresets.HalfVertical, Apps = [new AppAssignment { Executable = "a.exe", RegionIndex = 5 }] };
        Assert.Contains(p.Validate(), e => e.Contains("existiert im Layout nicht"));
    }

    [Fact]
    public void ExportImport_RoundTrips()
    {
        var p = new Profile { Name = "Arbeit", Layout = LayoutPresets.Grid2x2 };
        var back = ProfileStore.Import(ProfileStore.Export(p));
        Assert.Equal("Arbeit", back.Name);
        Assert.Equal(4, back.Layout.Regions.Count);
    }
}
