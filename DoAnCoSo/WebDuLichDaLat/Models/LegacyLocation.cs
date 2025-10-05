using System.ComponentModel.DataAnnotations;

namespace WebDuLichDaLat.Models
{
    public class LegacyLocation
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(100)]
        public string OldName { get; set; }  // Ví dụ: "Long An"

        [Required]
        [StringLength(100)]
        public string CurrentName { get; set; } // Ví dụ: "Tây Ninh"

        public double Latitude { get; set; }
        public double Longitude { get; set; }

        // Quan hệ ngược
        public ICollection<TransportPriceHistory> PriceHistories { get; set; } = new List<TransportPriceHistory>();
    }
}
