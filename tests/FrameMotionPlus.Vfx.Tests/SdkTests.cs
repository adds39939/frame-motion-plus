using FrameMotionPlus.Vfx.Tests.Support;

namespace FrameMotionPlus.Vfx.Tests;

/// <summary>Finding the SDK, reporting it unavailable, and loading it from its directory alone.</summary>
public class SdkTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fmp-vfx-tests-nowhere"));

    [Fact]
    public void An_explicit_directory_wins_over_the_settings_file_and_the_defaults()
    {
        var location = VfxSdkLocator.Resolve(explicitDirectory: "/opt/a", settingsDirectory: "/opt/b");
        Assert.Equal(Path.GetFullPath("/opt/a"), location.Directory);
        Assert.Equal(VfxSdkSource.Explicit, location.Source);
        Assert.Equal(Path.Combine(Path.GetFullPath("/opt/a"), "features", "nvvfxvideoframegeneration"), location.FeatureDirectory);
        Assert.Contains("given explicitly", location.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_environment_variable_wins_over_the_settings_file_and_a_checkout()
    {
        var before = Environment.GetEnvironmentVariable(VfxSdkLocator.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(VfxSdkLocator.EnvironmentVariable, "/opt/from-env");
            var location = VfxSdkLocator.Resolve(settingsDirectory: "/opt/b");
            Assert.Equal(Path.GetFullPath("/opt/from-env"), location.Directory);
            Assert.Equal(VfxSdkSource.Environment, location.Source);

            Environment.SetEnvironmentVariable(VfxSdkLocator.EnvironmentVariable, null);
            var settings = VfxSdkLocator.Resolve(settingsDirectory: "/opt/b");
            Assert.Equal(VfxSdkSource.Settings, settings.Source);
            Assert.Equal(Path.GetFullPath("/opt/b"), settings.Directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VfxSdkLocator.EnvironmentVariable, before);
        }
    }

    [Fact]
    public void Without_any_setting_a_checkout_uses_its_lib_directory_else_the_installer_default()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(VfxSdkLocator.EnvironmentVariable)))
        {
            Assert.Skip($"${VfxSdkLocator.EnvironmentVariable} is set for this run");
        }

        var location = Sdk.Location;
        var checkout = VfxSdkLocator.FindDevCheckout(FrameMotionPlus.Cuda.Tests.Support.Repo.Root);
        if (checkout is not null)
        {
            Assert.Equal(VfxSdkSource.DevCheckout, location.Source);
            Assert.Equal(checkout, location.Directory);
            Assert.EndsWith(Path.Combine("lib", "VideoFX"), location.Directory, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(VfxSdkSource.SystemDefault, location.Source);
            Assert.Equal(Path.GetFullPath(VfxSdkLocator.SystemDefault), location.Directory);
        }
    }

    [Fact]
    public void A_directory_without_the_sdk_is_reported_unavailable_with_a_reason_and_loads_nothing()
    {
        var empty = Directory.CreateTempSubdirectory("fmp-vfx-empty-");
        try
        {
            var location = VfxSdkLocation.At(empty.FullName);
            var missing = VfxSdk.MissingFiles(location);
            Assert.NotEmpty(missing);
            Assert.Contains(missing, m => m.EndsWith(OperatingSystem.IsWindows() ? "NVVideoEffects.dll" : "libVideoFX.so", StringComparison.Ordinal));

            Assert.False(VfxSdk.TryLoad(location, out var reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.DoesNotContain('\n', reason);
            Assert.Contains(empty.FullName, reason, StringComparison.Ordinal);
            if (!VfxSdk.IsLoaded)
            {
                // Refused before loading anything: a later attempt elsewhere is still possible.
                Assert.Contains("incomplete", reason, StringComparison.Ordinal);
                Assert.Empty(VfxSdk.LoadedLibraries);
            }

            Assert.False(VfxSdk.TryLoad(VfxSdkLocation.At(Path.Combine(Root, "does-not-exist")), out var none));
            Assert.Contains("does-not-exist", none, StringComparison.Ordinal);
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_missing_feature_is_named_in_the_reason()
    {
        var location = VfxSdkHardwareLocation();
        var withoutFeature = location with { FeatureDirectory = Path.Combine(Root, "no-feature") };
        var missing = VfxSdk.MissingFiles(withoutFeature);
        Assert.Single(missing);
        Assert.EndsWith("libnvVFXVideoFrameGeneration.so", missing[0], StringComparison.Ordinal);
        if (!VfxSdk.IsLoaded)
        {
            Assert.False(VfxSdk.TryLoad(withoutFeature, out var reason));
            Assert.Contains("features/nvvfxvideoframegeneration", reason, StringComparison.Ordinal);
            Assert.False(VfxSdk.IsLoaded);
        }
    }

    [Fact]
    [Trait(Category.Name, Category.Vfx)]
    public void The_sdk_loads_from_its_directory_alone_dependencies_first()
    {
        var env = VfxHardware.Require();
        Assert.True(VfxSdk.IsLoaded);
        Assert.Equal("1.3.0", VfxSdk.Version);
        Assert.Equal(env.Location.Directory, VfxSdk.Location!.Directory);

        // Nothing from the SDK came off the loader's search path: the test host was started without LD_LIBRARY_PATH
        // pointing there, and every library was opened by absolute path, the runtime before the SDK before the feature.
        var ldPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
        Assert.DoesNotContain("VideoFX", ldPath, StringComparison.Ordinal);
        var loaded = VfxSdk.LoadedLibraries;
        Log.WriteLine(string.Join("\n", loaded));
        Assert.All(loaded, path => Assert.True(Path.IsPathRooted(path), path));
        var order = loaded.Select(Path.GetFileName).ToList();
        Assert.True(order.IndexOf("libcudart.so.12") < order.IndexOf("libNVCVImage.so"));
        Assert.True(order.IndexOf("libNVCVImage.so") < order.IndexOf("libVideoFXLocal.so"));
        Assert.True(order.IndexOf("libVideoFXLocal.so") < order.IndexOf("libVideoFX.so"));
        Assert.Equal("libnvVFXVideoFrameGeneration.so", order[^1]);
        Assert.All(loaded.SkipLast(1), path => Assert.StartsWith(env.Location.Directory, path, StringComparison.Ordinal));
        Assert.StartsWith(env.Location.FeatureDirectory, loaded[^1], StringComparison.Ordinal);

        // A second load of the same directory is a no-op; another directory is refused while this one is loaded.
        Assert.True(VfxSdk.TryLoad(env.Location, out var again), again);
        Assert.False(VfxSdk.TryLoad(VfxSdkLocation.At(Root), out var other));
        Assert.Contains("already loaded", other, StringComparison.Ordinal);
    }

    private static VfxSdkLocation VfxSdkHardwareLocation()
    {
        var location = Sdk.Location;
        if (VfxSdk.MissingFiles(location).Count > 0)
        {
            Assert.Skip($"no VFX SDK at {location}");
        }

        return location;
    }
}
