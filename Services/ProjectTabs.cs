using System;
using System.Collections.Generic;

namespace SuperDev.Services;

public readonly record struct OpenChecklistTab(
    Guid DocumentId,
    Guid? OpenedFromProjectId,
    bool IsPinned);

public readonly record struct TabFocus(Guid? DocumentId, Guid? ProjectId)
{
    public static TabFocus Home { get; } = new(null, null);

    public bool IsHome => DocumentId is null && ProjectId is null;
}

public readonly record struct TabCloseRequest(Guid DocumentId, bool HasPath, bool IsDirty);

public enum TabCloseStepKind
{
    /// <summary>A saved checklist with unsaved edits. Write it. Do not prompt, and do not drop the edits.</summary>
    SaveExisting,

    /// <summary>An unsaved checklist with edits. Ask to save, discard, or cancel.</summary>
    Prompt,

    /// <summary>Remove the tab. These steps come after every save and prompt so a cancel closes nothing.</summary>
    Close
}

public readonly record struct TabCloseStep(TabCloseStepKind Kind, Guid DocumentId);

/// <summary>
/// Decisions for project and checklist tabs. The window keeps the real tab controls
/// and applies these results, so tab identity stays on document and project ids.
/// </summary>
public static class TabSession
{
    public static bool ShouldOpenNew(IReadOnlyList<OpenChecklistTab> open, Guid documentId)
    {
        foreach (var tab in open)
        {
            if (tab.DocumentId == documentId)
                return false;
        }

        return true;
    }

    /// <summary>
    /// An already-open checklist keeps the tab it has. A new open uses the
    /// project it was opened from, which is null for a standalone open.
    /// </summary>
    public static Guid? ParentFor(bool alreadyOpen, Guid? existingParent, Guid? requestedParent) =>
        alreadyOpen ? existingParent : requestedParent;

    public static bool TryFind(
        IReadOnlyList<OpenChecklistTab> open,
        Guid documentId,
        out OpenChecklistTab tab)
    {
        foreach (var candidate in open)
        {
            if (candidate.DocumentId != documentId)
                continue;

            tab = candidate;
            return true;
        }

        tab = default;
        return false;
    }

    public static List<Guid> Children(IReadOnlyList<OpenChecklistTab> open, Guid projectId)
    {
        var children = new List<Guid>();
        foreach (var tab in open)
        {
            if (tab.OpenedFromProjectId == projectId)
                children.Add(tab.DocumentId);
        }

        return children;
    }

    public static List<OpenChecklistTab> ExceptProject(
        IReadOnlyList<OpenChecklistTab> open,
        Guid projectId)
    {
        var remaining = new List<OpenChecklistTab>();
        foreach (var tab in open)
        {
            if (tab.OpenedFromProjectId != projectId)
                remaining.Add(tab);
        }

        return remaining;
    }

    /// <summary>
    /// Drop the project association on open tabs without closing them.
    /// Used when the project itself is deleted.
    /// </summary>
    public static List<OpenChecklistTab> ReleaseProject(
        IReadOnlyList<OpenChecklistTab> open,
        Guid projectId)
    {
        var released = new List<OpenChecklistTab>(open.Count);
        foreach (var tab in open)
        {
            released.Add(tab.OpenedFromProjectId == projectId
                ? tab with { OpenedFromProjectId = null }
                : tab);
        }

        return released;
    }

    public static TabFocus AfterChecklistClosed(
        IReadOnlyList<OpenChecklistTab> open,
        IReadOnlyList<Guid> projectOrder,
        TabFocus active,
        Guid closedDocumentId)
    {
        if (active.DocumentId != closedDocumentId)
            return active;

        if (!TryFind(open, closedDocumentId, out var closed))
            return TabFocus.Home;

        if (closed.OpenedFromProjectId is Guid projectId)
        {
            var siblings = new List<OpenChecklistTab>();
            var index = -1;
            foreach (var tab in open)
            {
                if (tab.OpenedFromProjectId != projectId)
                    continue;

                if (tab.DocumentId == closedDocumentId)
                    index = siblings.Count;
                else
                    siblings.Add(tab);
            }

            if (siblings.Count > 0)
            {
                var next = index < 0 ? 0 : Math.Min(index, siblings.Count - 1);
                return new TabFocus(siblings[next].DocumentId, null);
            }

            return new TabFocus(null, projectId);
        }

        var remaining = new List<OpenChecklistTab>();
        var oldIndex = 0;
        var seen = false;
        foreach (var tab in open)
        {
            if (tab.DocumentId == closedDocumentId)
            {
                seen = true;
                continue;
            }

            if (!seen)
                oldIndex++;

            remaining.Add(tab);
        }

        if (remaining.Count == 0)
        {
            if (projectOrder.Count > 0)
                return new TabFocus(null, projectOrder[0]);

            return TabFocus.Home;
        }

        var nextIndex = Math.Min(oldIndex, remaining.Count - 1);
        return new TabFocus(remaining[nextIndex].DocumentId, null);
    }

    public static TabFocus AfterProjectClosed(
        IReadOnlyList<OpenChecklistTab> open,
        IReadOnlyList<Guid> projectOrder,
        TabFocus active,
        Guid closedProjectId)
    {
        var childIds = new HashSet<Guid>();
        foreach (var tab in open)
        {
            if (tab.OpenedFromProjectId == closedProjectId)
                childIds.Add(tab.DocumentId);
        }

        var closingActive = active.ProjectId == closedProjectId
            || (active.DocumentId is Guid documentId && childIds.Contains(documentId));
        if (!closingActive)
            return active;

        var others = new List<Guid>();
        var projectIndex = 0;
        var found = false;
        foreach (var projectId in projectOrder)
        {
            if (projectId == closedProjectId)
            {
                found = true;
                continue;
            }

            if (!found)
                projectIndex++;

            others.Add(projectId);
        }

        if (others.Count > 0)
        {
            var next = found ? Math.Min(projectIndex, others.Count - 1) : 0;
            return new TabFocus(null, others[next]);
        }

        foreach (var tab in open)
        {
            if (!childIds.Contains(tab.DocumentId))
                return new TabFocus(tab.DocumentId, null);
        }

        return TabFocus.Home;
    }
}

public static class ProjectClose
{
    public static List<TabCloseStep> Plan(IReadOnlyList<TabCloseRequest> children)
    {
        var steps = new List<TabCloseStep>();
        foreach (var child in children)
        {
            if (!child.IsDirty)
                continue;

            steps.Add(new TabCloseStep(
                child.HasPath ? TabCloseStepKind.SaveExisting : TabCloseStepKind.Prompt,
                child.DocumentId));
        }

        foreach (var child in children)
            steps.Add(new TabCloseStep(TabCloseStepKind.Close, child.DocumentId));

        return steps;
    }
}
