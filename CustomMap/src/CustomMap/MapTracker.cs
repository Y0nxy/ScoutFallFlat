using Photon.Pun;
using ExitGames.Client.Photon; // Required for Hashtable

namespace ScoutFallFlat
{
    public static class MapTracker
    {
        private const string MapKey = "CurrentMap";

        public static string GetMapName()
        {
            // Check if we are in a room and if the room has our map key saved
            if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(MapKey, out object name))
            {
                return name as string ?? "Default";
            }

            return "Default";
        }

        public static void SendMapName(string name)
        {
            // Guard clause: Stop here if we aren't the host or in a room
            if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient) return;

            var props = new Hashtable { { MapKey, name } };
            PhotonNetwork.CurrentRoom.SetCustomProperties(props);
        }
    }
}
