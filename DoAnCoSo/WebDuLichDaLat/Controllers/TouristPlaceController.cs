using WebDuLichDaLat.Areas.Admin.Controllers.Repositories;
using WebDuLichDaLat.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace WebDuLichDaLat.Controllers
{
    public class TouristPlaceController : Controller
    {
        private readonly ITouristPlaceRepository _touristPlaceRepository;
        private readonly ICategoryRepository _categoryRepository;
        private readonly IRegionRepository _regionRepository;
        private readonly ApplicationDbContext _context;

        public TouristPlaceController(
            ITouristPlaceRepository touristPlaceRepository,
            ICategoryRepository categoryRepository,
            IRegionRepository regionRepository,
            ApplicationDbContext context)
        {
            _touristPlaceRepository = touristPlaceRepository;
            _categoryRepository = categoryRepository;
            _regionRepository = regionRepository;
            _context = context;
        }

        // Hiển thị danh sách Địa điểm có lọc theo danh mục và Địa điểm
        public IActionResult Index(int? categoryId, int? regionId)
        {
            var categories = _categoryRepository.GetAllCategories();
            var regions = _regionRepository.GetAllRegions();

            ViewBag.Categories = categories;
            ViewBag.Regions = regions;

            var allTouristPlaces = _touristPlaceRepository.GetAll();

            if (categoryId.HasValue)
                allTouristPlaces = allTouristPlaces.Where(p => p.CategoryId == categoryId.Value);

            if (regionId.HasValue)
                allTouristPlaces = allTouristPlaces.Where(p => p.RegionId == regionId.Value);

            return View(allTouristPlaces);
        }

        // Chi tiết Địa điểm + đánh giá
        public IActionResult Display(string id)
        {
            var touristPlace = _context.TouristPlaces
                .Include(p => p.Reviews)
                .Include(p => p.Category)
                .Include(p => p.Region)
                .FirstOrDefault(p => p.Id == id);

            if (touristPlace == null)
                return NotFound();

            // Tính trung bình và tổng số đánh giá
            if (touristPlace.Reviews != null && touristPlace.Reviews.Any())
            {
                ViewBag.AverageRating = touristPlace.Reviews.Average(r => r.Rating);
                ViewBag.RatingCount = touristPlace.Reviews.Count();
            }
            else
            {
                ViewBag.Average
                    
                    = 0;
                ViewBag.RatingCount = 0;
            }

            return View(touristPlace);
        }

        // Tìm kiếm Địa điểm theo từ khóa
        public IActionResult Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return RedirectToAction("Index");

            var touristPlaces = _touristPlaceRepository.GetAll()
                .Where(p =>
                    (!string.IsNullOrEmpty(p.Name) && p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(p.Description) && p.Description.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            ViewBag.Categories = _categoryRepository.GetAllCategories();
            ViewBag.Regions = _regionRepository.GetAllRegions();

            return View("Index", touristPlaces);
        }

        // Trang riêng tư
        public IActionResult Privacy()
        {
            return View();
        }

        // API: Gửi đánh giá

        [HttpPost]
        [Authorize]
        public IActionResult Rate(string touristPlaceId, int rating)
        {
            if (string.IsNullOrEmpty(touristPlaceId) || rating < 1 || rating > 5)
                return BadRequest();

            var touristPlace = _context.TouristPlaces.Find(touristPlaceId);
            if (touristPlace == null)
                return NotFound();

            var review = new Review
            {
                TouristPlaceId = touristPlaceId,
                Rating = rating,
                CreatedAt = DateTime.Now
            };

            _context.Reviews.Add(review);
            _context.SaveChanges();

            return Ok();
        }
    }
}
