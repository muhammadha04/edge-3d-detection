// Screens of the Annotate panel (A0: Home, Room, Catalog, ground-truth rule). Each screen only describes its
// content; AnnotationMode owns the data and rebuilds the screen after every change.
using System.Collections.Generic;
using UnityEngine;

namespace QuestDetect3D.Annotation
{
    public sealed class HomeScreen : IAnnScreen
    {
        private readonly AnnotationMode m_mode;

        public HomeScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => $"QD3D Annotate  v{Application.version}";

        public void Build(AnnPanel p)
        {
            var room = m_mode.CurrentRoom;
            p.Label(room != null
                    ? $"Room: <b>{room.Name}</b>   ({(string.IsNullOrEmpty(room.AnchorUuid) ? "room cross not placed yet" : "room cross placed")})"
                    : "Room: <b>none</b>  -  create or select one first",
                AnnPanel.TextSize, room != null ? AnnPanel.TextColor : AnnPanel.Warning);
            p.Label($"Objects in catalog: <b>{m_mode.Catalog.Objects.Count}</b>");
            p.Space();

            p.BeginRow();
            p.Button("Room", () => m_mode.Show(new RoomScreen(m_mode)), true, AnnPanel.PrimaryButton);
            p.Button("Catalog", () => m_mode.Show(new CatalogScreen(m_mode)), true, AnnPanel.PrimaryButton);
            p.EndRow();
            p.BeginRow();
            p.Button("Calibrate tip", null, false);
            p.Button("Annotate", null, false);
            p.EndRow();
            p.BeginRow();
            p.Button("Verify", null, false);
            p.Button("Record", null, false);
            p.Button("Export", null, false);
            p.EndRow();
            p.BeginRow();
            p.Button("Ground-truth rule", () => m_mode.Show(new RuleScreen(m_mode)));
            p.Button("Exit to start menu", m_mode.RequestExit, true, AnnPanel.DangerButton);
            p.EndRow();
            p.Space();
            p.Label("Trigger = click.  Left Menu button = bring this panel in front of you.\nGrey buttons arrive in later milestones.",
                22, AnnPanel.DimText);
        }
    }

    public sealed class RoomScreen : IAnnScreen
    {
        private const int PageSize = 3;
        private readonly AnnotationMode m_mode;
        private int m_page;

        public RoomScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => "Room";

        public void Build(AnnPanel p)
        {
            var rooms = m_mode.Store.ListRooms();
            var current = m_mode.CurrentRoom;
            p.Label(current != null ? $"Current room: <b>{current.Name}</b>  (id {current.Id})" : "No room selected.",
                AnnPanel.TextSize, current != null ? AnnPanel.TextColor : AnnPanel.Warning);
            p.Label(rooms.Count == 0 ? "No rooms yet. Press New room." : $"{rooms.Count} room(s) saved. Select one:", 24, AnnPanel.DimText);

            var pages = Mathf.Max(1, (rooms.Count + PageSize - 1) / PageSize);
            m_page = Mathf.Clamp(m_page, 0, pages - 1);
            for (var i = m_page * PageSize; i < Mathf.Min(rooms.Count, (m_page + 1) * PageSize); i++)
            {
                var room = rooms[i];
                var isCurrent = current != null && current.Id == room.Id;
                p.Button(isCurrent ? $"{room.Name}   [current]" : room.Name, () =>
                {
                    m_mode.SetRoom(room);
                    m_mode.Refresh();
                }, true, isCurrent ? AnnPanel.PrimaryButton : AnnPanel.ButtonColor);
            }

            Pager.Build(p, m_page, pages, delta =>
            {
                m_page += delta;
                m_mode.Refresh();
            });

            p.Space();
            p.BeginRow();
            p.Button("New room", () =>
            {
                m_mode.NewRoom();
                m_page = int.MaxValue; // jump to the page with the new room
                m_mode.Refresh();
            }, true, AnnPanel.PrimaryButton);
            p.Button("Back", () => m_mode.Show(new HomeScreen(m_mode)));
            p.EndRow();
            p.Label("Automatic names for now (keyboard: next milestone). Room cross and drift check come with the room frame.",
                22, AnnPanel.DimText);
        }
    }

    public sealed class CatalogScreen : IAnnScreen
    {
        private const int PageSize = 6;
        private readonly AnnotationMode m_mode;
        private int m_page;

        public CatalogScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => "Catalog";

        public void Build(AnnPanel p)
        {
            var objects = m_mode.Catalog.Objects;
            p.Label(objects.Count == 0 ? "No objects yet. Press Add object." : $"{objects.Count} object(s):", 24, AnnPanel.DimText);

            var pages = Mathf.Max(1, (objects.Count + PageSize - 1) / PageSize);
            m_page = Mathf.Clamp(m_page, 0, pages - 1);
            for (var i = m_page * PageSize; i < Mathf.Min(objects.Count, (m_page + 1) * PageSize); i++)
            {
                var o = objects[i];
                var tape = o.TapeDimsCm != null && o.TapeDimsCm.IsSet
                    ? $"{o.TapeDimsCm.W:0.#} x {o.TapeDimsCm.D:0.#} x {o.TapeDimsCm.H:0.#} cm"
                    : "not measured";
                var reps = m_mode.CurrentRoom != null ? $"{m_mode.RepetitionCount(o.Id)} annotation(s) in {m_mode.CurrentRoom.Name}" : "no room selected";
                p.Label($"<b>{o.Name}</b>   {o.Category}   tape: {tape}   {reps}");
            }

            Pager.Build(p, m_page, pages, delta =>
            {
                m_page += delta;
                m_mode.Refresh();
            });

            p.Space();
            p.BeginRow();
            p.Button("Add object", () =>
            {
                m_mode.NewObject();
                m_page = int.MaxValue;
                m_mode.Refresh();
            }, true, AnnPanel.PrimaryButton);
            p.Button("Back", () => m_mode.Show(new HomeScreen(m_mode)));
            p.EndRow();
            p.Label("Objects get automatic names for now. Name, category, tape W/D/H, symmetry, notes and photo: next milestone.",
                22, AnnPanel.DimText);
        }
    }

    public sealed class RuleScreen : IAnnScreen
    {
        private readonly AnnotationMode m_mode;

        public RuleScreen(AnnotationMode mode) => m_mode = mode;

        public string Title => "Ground-truth rule";

        public void Build(AnnPanel p)
        {
            p.Label(GroundTruth.Definition, 30);
            p.Space();
            p.Label("Stored with every annotation file and recording (definition v" + GroundTruth.DefinitionVersion + ").", 22, AnnPanel.DimText);
            p.Label(GroundTruth.BoxConvention, 22, AnnPanel.DimText);
            p.Space();
            p.Button("Back", () => m_mode.Show(new HomeScreen(m_mode)));
        }
    }

    public static class Pager
    {
        public static void Build(AnnPanel p, int page, int pages, System.Action<int> step)
        {
            if (pages <= 1)
            {
                return;
            }

            p.BeginRow();
            p.Button("< Prev", () => step(-1), page > 0);
            p.Button($"Page {page + 1} / {pages}", null, false);
            p.Button("Next >", () => step(1), page < pages - 1);
            p.EndRow();
        }
    }
}
