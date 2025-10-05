namespace WebDuLichDaLat.Models
{
    public class PlaceCluster
    {
        public List<TouristPlace> Places { get; set; } = new List<TouristPlace>();
        public int RecommendedNights { get; set; }
    }

}