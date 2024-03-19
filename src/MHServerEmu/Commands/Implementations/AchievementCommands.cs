﻿using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.System;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Frontend;
using MHServerEmu.Games.Achievements;
using MHServerEmu.Grouping;
using MHServerEmu.PlayerManagement;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("achievement", "Manages achievements.", AccountUserLevel.User)]
    public class AchievementCommands : CommandGroup
    {
        [Command("unlock", "Unlocks an achievement.\nUsage: achievement unlock [id]", AccountUserLevel.User)]
        public string Unlock(string[] @params, FrontendClient client)
        {
            if (client == null)
                return "You can only invoke this command from the game.";

            if (@params.IsNullOrEmpty())
                return "Invalid arguments. Type 'help achievement unlock' to get help.";

            if (uint.TryParse(@params[0], out uint id) == false)
                return "Failed to parse achievement id.";

            AchievementInfo info = AchievementDatabase.Instance.GetAchievementInfoById(id);

            if (info == null)
                return $"Invalid achievement id {id}.";

            if (info.Enabled == false)
                return $"Achievement id {id} is disabled.";

            var playerManager = ServerManager.Instance.GetGameService(ServerType.PlayerManager) as PlayerManagerService;
            var game = playerManager.GetGameByPlayer(client);
            var playerConnection = game.NetworkManager.GetPlayerConnection(client);

            AchievementState state = playerConnection.Player.AchievementState;
            state.SetAchievementProgress(id, new(info.Threshold, Clock.UnixTime));
            client.SendMessage(1, state.ToUpdateMessage(true));
            return string.Empty;
        }

        [Command("info", "Outputs info for the specified achievement.\nUsage: achievement info [id]", AccountUserLevel.User)]
        public string Info(string[] @params, FrontendClient client)
        {
            if (@params.IsNullOrEmpty())
                return "Invalid arguments. Type 'help achievement unlock' to get help.";

            if (uint.TryParse(@params[0], out uint id) == false)
                return "Failed to parse achievement id.";

            AchievementInfo info = AchievementDatabase.Instance.GetAchievementInfoById(id);

            if (info == null)
                return $"Invalid achievement id {id}.";

            // Output as a single string with line breaks if the command was invoked from the console
            if (client == null)
                return info.ToString();

            // Output as a list of chat messages if the command was invoked from the in-game chat.
            ChatHelper.SendMetagameMessage(client, "Achievement Info:");
            ChatHelper.SendMetagameMessages(client, info.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries), false);
            return string.Empty;
        }
    }
}
