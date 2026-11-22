using System;
using Imui.Controls;
using Imui.Core;
using ZeepSDK.UI;

namespace CouchParty
{
    /// <summary>Adds a "Couch Party" menu to ZeepSDK's toolbar (if the user has a toolbar key set in ZeepSDK).</summary>
    internal class Toolbar : IZeepToolbarDrawer
    {
        public string MenuTitle => "Couch Party";

        public void DrawMenuItems(ImGui gui)
        {
            if (gui.Menu(Span("Players & profiles"))) CouchUI.Instance?.Open(CouchUI.Tab.Players);
            if (gui.Menu(Span("Leaderboard"))) CouchUI.Instance?.Open(CouchUI.Tab.Leaderboard);
        }

        // Avoids string.AsSpan(), which is defined twice in the game's libraries.
        private static ReadOnlySpan<char> Span(string text) => new ReadOnlySpan<char>(text.ToCharArray());
    }
}
