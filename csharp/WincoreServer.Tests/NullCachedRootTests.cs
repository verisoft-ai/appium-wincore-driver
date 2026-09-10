using WincoreServer.Commands;
using WincoreServer.State;
using Xunit;

namespace WincoreServer.Tests;

/// <summary>
/// A stale/disconnected UIA root makes <c>IUIAutomationElement.FindFirstBuildCache</c>
/// hand back a null element (the standard "no match" COM result, not an exception).
/// <see cref="PageSourceCommands.GetPageSource"/> and <see cref="UiaXmlModel.Build"/>
/// each wrap their cached walk in a try/catch that's meant to fall back to the live
/// walk on any failure — but <c>BuildPageSourceCached</c>/<c>BuildElementCached</c>
/// treat every property read defensively (so a normal stale element never crashes
/// mid-walk), which means a null root doesn't throw either: it silently produces a
/// bogus single-element document instead of tripping the fallback.
///
/// No COM mock needed here — IUIAutomationElement is a reference type, so `null` is a
/// legal argument. These call the internal seams split out of GetPageSource/Build
/// directly (see AssemblyInfo.cs for the InternalsVisibleTo), bypassing the need to
/// fake FindFirstBuildCache itself.
/// </summary>
public class NullCachedRootTests
{
    [Fact]
    public void BuildCachedPageSourceXml_NullRoot_ThrowsInsteadOfSwallowing()
    {
        var state = new SessionState();
        var req = state.Automation.CreateCacheRequest();
        var trueCond = state.Automation.CreateTrueCondition();

        Assert.Throws<InvalidOperationException>(() =>
            PageSourceCommands.BuildCachedPageSourceXml(null, state, req, trueCond));
    }

    [Fact]
    public void UiaXmlModel_BuildFromCachedRoot_NullRoot_ThrowsInsteadOfSwallowing()
    {
        var state = new SessionState();
        var req = state.Automation.CreateCacheRequest();
        var trueCond = state.Automation.CreateTrueCondition();

        Assert.Throws<InvalidOperationException>(() =>
            UiaXmlModel.BuildFromCachedRoot(null, null, req, trueCond));
    }
}
