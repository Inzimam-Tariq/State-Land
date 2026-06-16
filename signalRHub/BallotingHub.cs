using Microsoft.AspNetCore.SignalR;

namespace StateLand.Hubs
{
    public class BallotingHub : Hub
    {
        // ============================================
        // CLIENT CONNECTED
        // ============================================

        public override async Task OnConnectedAsync()
        {
            var connectionId = Context.ConnectionId;

            // Send only to connected client
            await Clients.Caller.SendAsync(
                "Connected",
                new
                {
                    Status = "Connected",
                    ConnectionId = connectionId,
                    Message = "Connected To Balloting Hub"
                });

            // Notify all clients
            await Clients.All.SendAsync(
                "UserConnected",
                new
                {
                    ConnectionId = connectionId,
                    Message = "New User Connected"
                });

            await base.OnConnectedAsync();
        }

        // ============================================
        // CLIENT DISCONNECTED
        // ============================================

        public override async Task OnDisconnectedAsync(
            Exception? exception)
        {
            var connectionId = Context.ConnectionId;

            await Clients.All.SendAsync(
                "UserDisconnected",
                new
                {
                    ConnectionId = connectionId,
                    Message = "User Disconnected"
                });

            await base.OnDisconnectedAsync(exception);
        }

        // ============================================
        // OPTIONAL: JOIN GROUP
        // ============================================

        public async Task JoinBallotingGroup(string groupName)
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                groupName);

            await Clients.Caller.SendAsync(
                "JoinedGroup",
                new
                {
                    Group = groupName,
                    Message = $"Joined Group: {groupName}"
                });
        }

        // ============================================
        // OPTIONAL: LEAVE GROUP
        // ============================================

        public async Task LeaveBallotingGroup(string groupName)
        {
            await Groups.RemoveFromGroupAsync(
                Context.ConnectionId,
                groupName);

            await Clients.Caller.SendAsync(
                "LeftGroup",
                new
                {
                    Group = groupName,
                    Message = $"Left Group: {groupName}"
                });
        }
    }
}