using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace EduTrackTrial.Hubs
{
    public class NotificationHub : Hub
    {
        public Task SubscribeToStudent(string studentGroup)
        {
            return Groups.AddToGroupAsync(Context.ConnectionId, studentGroup);
        }

        public Task UnsubscribeFromStudent(string studentGroup)
        {
            return Groups.RemoveFromGroupAsync(Context.ConnectionId, studentGroup);
        }
    }
}