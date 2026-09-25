using System.Text.Json;
using WincoreServer.State;
using WincoreServer.Uia3;

namespace WincoreServer.Commands;

/// <summary>
/// UIA side of the pattern commands. Element ids are resolved (and tree-provider
/// elements routed away) by <see cref="Server.ElementRoute"/> before these run.
/// </summary>
public static class PatternCommands
{
    public static object? Invoke(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        if (element.GetCurrentPattern(UIA.InvokePatternId) is IUIAutomationInvokePattern invoke)
        {
            invoke.Invoke();
        }
        else if (element.GetCurrentPattern(UIA.SelectionItemPatternId) is IUIAutomationSelectionItemPattern sel)
        {
            sel.Select();
        }
        else if (element.GetCurrentPattern(UIA.LegacyIAccessiblePatternId) is IUIAutomationLegacyIAccessiblePattern legacy)
        {
            legacy.DoDefaultAction();
        }
        else
        {
            throw new InvalidOperationException(
                "Element does not support InvokePattern, SelectionItemPattern, or LegacyIAccessiblePattern.");
        }

        // Yield to let the target app's message pump process the event before
        // the next command touches it. Keeps rapid back-to-back invokes from
        // racing the app's UI thread (e.g. calculator button mashing).
        Thread.Sleep(50);
        return null;
    }

    public static object? Expand(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        if (element.GetCurrentPattern(UIA.ExpandCollapsePatternId) is IUIAutomationExpandCollapsePattern expandPattern)
        {
            expandPattern.Expand();
            return null;
        }

        // Fallback for legacy controls without ExpandCollapsePattern. State poll
        // below is log-only (many controls never set the bit) — must not gate
        // success, or a false negative triggers ALT+Down and closes the popup.
        if (element.GetCurrentPattern(UIA.LegacyIAccessiblePatternId) is IUIAutomationLegacyIAccessiblePattern legacy)
        {
            legacy.DoDefaultAction();

            const int StateSystemExpanded = 0x1000;
            Thread.Sleep(50);
            if ((legacy.CurrentState & StateSystemExpanded) == 0)
            {
                var elementId = parameters.GetProperty("elementId").GetString();
                Console.Error.WriteLine(
                    $"[Expand] DoDefaultAction fired on '{elementId}' but STATE_SYSTEM_EXPANDED never observed " +
                    "(control may not report expanded state — not treated as a failure).");
            }

            return null;
        }

        throw new InvalidOperationException("Element does not support ExpandCollapsePattern.");
    }

    public static object? Collapse(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        Require<IUIAutomationExpandCollapsePattern>(element, UIA.ExpandCollapsePatternId, "ExpandCollapsePattern").Collapse();
        return null;
    }

    public static object? Toggle(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        Require<IUIAutomationTogglePattern>(element, UIA.TogglePatternId, "TogglePattern").Toggle();
        return null;
    }

    public static object? GetToggleState(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        return Require<IUIAutomationTogglePattern>(element, UIA.TogglePatternId, "TogglePattern")
            .CurrentToggleState.ToString();
    }

    public static object? SetRangeValue(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        var value = parameters.GetProperty("value").GetDouble();
        Require<IUIAutomationRangeValuePattern>(element, UIA.RangeValuePatternId, "RangeValuePattern").SetValue(value);
        return null;
    }

    public static object? ScrollIntoView(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        Require<IUIAutomationScrollItemPattern>(element, UIA.ScrollItemPatternId, "ScrollItemPattern").ScrollIntoView();
        return null;
    }

    public static object? Select(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        Require<IUIAutomationSelectionItemPattern>(element, UIA.SelectionItemPatternId, "SelectionItemPattern").Select();
        return null;
    }

    public static object? AddToSelection(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        Require<IUIAutomationSelectionItemPattern>(element, UIA.SelectionItemPatternId, "SelectionItemPattern").AddToSelection();
        return null;
    }

    public static object? RemoveFromSelection(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        Require<IUIAutomationSelectionItemPattern>(element, UIA.SelectionItemPatternId, "SelectionItemPattern").RemoveFromSelection();
        return null;
    }

    public static object? IsSelected(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        return Require<IUIAutomationSelectionItemPattern>(element, UIA.SelectionItemPatternId, "SelectionItemPattern")
            .CurrentIsSelected != 0;
    }

    public static object? IsMultipleSelect(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        return Require<IUIAutomationSelectionPattern>(element, UIA.SelectionPatternId, "SelectionPattern")
            .CurrentCanSelectMultiple != 0;
    }

    public static object? GetSelectedElements(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        var selected = Require<IUIAutomationSelectionPattern>(element, UIA.SelectionPatternId, "SelectionPattern")
            .GetCurrentSelection();
        return FindCommands.IterateArray(selected)
            .Select(el => state.SaveElementAndReturnId(el))
            .ToArray();
    }

    public static object? MaximizeWindow(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        Require<IUIAutomationWindowPattern>(element, UIA.WindowPatternId, "WindowPattern")
            .SetWindowVisualState(WindowVisualState.Maximized);
        return null;
    }

    public static object? MinimizeWindow(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        Require<IUIAutomationWindowPattern>(element, UIA.WindowPatternId, "WindowPattern")
            .SetWindowVisualState(WindowVisualState.Minimized);
        return null;
    }

    public static object? RestoreWindow(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        Require<IUIAutomationWindowPattern>(element, UIA.WindowPatternId, "WindowPattern")
            .SetWindowVisualState(WindowVisualState.Normal);
        return null;
    }

    public static object? CloseWindow(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        Require<IUIAutomationWindowPattern>(element, UIA.WindowPatternId, "WindowPattern").Close();
        return null;
    }

    public static object? MoveWindow(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        var x = parameters.GetProperty("x").GetDouble();
        var y = parameters.GetProperty("y").GetDouble();
        Require<IUIAutomationTransformPattern>(element, UIA.TransformPatternId, "TransformPattern").Move(x, y);
        return null;
    }

    public static object? ResizeWindow(SessionState state, IUIAutomationElement element, JsonElement parameters)
    {
        var width = parameters.GetProperty("width").GetDouble();
        var height = parameters.GetProperty("height").GetDouble();
        Require<IUIAutomationTransformPattern>(element, UIA.TransformPatternId, "TransformPattern").Resize(width, height);
        return null;
    }

    private static T Require<T>(IUIAutomationElement element, int patternId, string patternName) where T : class
    {
        if (element.GetCurrentPattern(patternId) is T pattern)
        {
            return pattern;
        }
        throw new InvalidOperationException($"Element does not support {patternName}.");
    }
}
