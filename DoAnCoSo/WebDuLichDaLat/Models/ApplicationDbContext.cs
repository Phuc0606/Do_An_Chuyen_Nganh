using WebDuLichDaLat.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace WebDuLichDaLat.Models
{
    public class ApplicationDbContext : IdentityDbContext<User>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        public DbSet<Category> Categories { get; set; }
        public DbSet<TouristPlace> TouristPlaces { get; set; }
        public DbSet<Region> Regions { get; set; }
        public DbSet<Favorite> Favorites { get; set; }
        public DbSet<BlogPost> BlogPosts { get; set; }
        public DbSet<Review> Reviews { get; set; }
        public DbSet<Contact> Contacts { get; set; }

        // Thêm các DbSet mới
        public DbSet<TransportOption> TransportOptions { get; set; }
        public DbSet<Hotel> Hotels { get; set; }
        public DbSet<Restaurant> Restaurants { get; set; }
        public DbSet<Attraction> Attractions { get; set; }
        public DbSet<LegacyLocation> LegacyLocations { get; set; }
        public DbSet<TransportPriceHistory> TransportPriceHistories { get; set; }
        public DbSet<LocalTransport> LocalTransports { get; set; }
        public DbSet<RoutePrice> RoutePrices { get; set; }
        public DbSet<NearbyPlace> NearbyPlaces { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Category>().HasData(
                new Category { Id = 1, Name = "Danh lam thắng cảnh" },
                new Category { Id = 2, Name = "Di tích lịch sử" },
                new Category { Id = 3, Name = "Làng nghề truyền thống" },
                new Category { Id = 4, Name = "Núi rừng" },
                new Category { Id = 5, Name = "Hồ - thác nước" }
            );

            modelBuilder.Entity<Region>().HasData(
                new Region { Id = 1, Name = "Trung tâm thành phố" },
                new Region { Id = 2, Name = "Langbiang" },
                new Region { Id = 3, Name = "Hồ Tuyền Lâm" },
                new Region { Id = 4, Name = "Xã Tà Nung" }
            );

            modelBuilder.Entity<Contact>(entity =>
            {
                entity.Property(c => c.Name).IsRequired().HasMaxLength(100);
                entity.Property(c => c.Email).IsRequired().HasMaxLength(100);
                entity.Property(c => c.Subject).IsRequired().HasMaxLength(150);
                entity.Property(c => c.Message).IsRequired();
            });

            modelBuilder.Entity<TouristPlace>()
                .HasOne(p => p.Category)
                .WithMany(c => c.TouristPlaces)
                .HasForeignKey(p => p.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<TouristPlace>()
                .HasOne(p => p.Region)
                .WithMany(c => c.TouristPlaces)
                .HasForeignKey(p => p.RegionId)
                .OnDelete(DeleteBehavior.SetNull);
            modelBuilder.Entity<TransportPriceHistory>()
              .HasOne(p => p.TransportOption)
              .WithMany(t => t.PriceHistories)
              .HasForeignKey(p => p.TransportOptionId);

            modelBuilder.Entity<TransportPriceHistory>()
                .HasOne(p => p.LegacyLocation)
                .WithMany(l => l.PriceHistories)
                .HasForeignKey(p => p.LegacyLocationId);
        }
    }
}
