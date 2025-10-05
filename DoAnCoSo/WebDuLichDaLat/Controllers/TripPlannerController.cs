using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WebDuLichDaLat.Models;

namespace WebDuLichDaLat.Controllers
{
    public class TripPlannerController : Controller
    {
        private readonly ApplicationDbContext _context;

        public TripPlannerController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public IActionResult Index()
        {
            var categories = _context.Categories.ToList();
            var touristPlaces = _context.TouristPlaces.ToList();

            var model = new TripPlannerViewModel
            {
                Categories = categories,
                TouristPlaces = touristPlaces,
                TransportOptions = _context.TransportOptions.ToList(),
                Hotels = _context.Hotels.ToList(),
                Restaurants = _context.Restaurants.ToList(),
                Attractions = _context.Attractions.ToList(),
                Suggestions = new List<string>()
            };

            model.TransportSelectList = model.TransportOptions
                .Select(t => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem
                {
                    Value = t.Id.ToString(),
                    Text = $"{t.Name} ({t.Type})"
                })
                .ToList();

            ViewBag.TouristPlacesJson = JsonConvert.SerializeObject(
                touristPlaces.Select(tp => new { tp.Id, tp.Name, tp.Latitude, tp.Longitude, tp.CategoryId })
            );

            return View(model);
        }

        [HttpPost]
        public IActionResult Index(TripPlannerViewModel model)
        {
            model.TransportOptions = _context.TransportOptions.ToList();
            model.TransportSelectList = model.TransportOptions
                .Select(t => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem
                {
                    Value = t.Id.ToString(),
                    Text = $"{t.Name} ({t.Type})"
                })
                .ToList();
            model.Hotels = _context.Hotels.ToList();
            model.Restaurants = _context.Restaurants.ToList();
            model.Attractions = _context.Attractions.ToList();
            model.TouristPlaces = _context.TouristPlaces.ToList();

            var selectedPlaceIds = model.SelectedTouristPlaceIds?
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList() ?? new List<string>();

            model.Suggestions = CalculateSuggestions(model, selectedPlaceIds);
            return View(model);
        }

        private List<string> CalculateSuggestions(TripPlannerViewModel model, List<string> selectedPlaceIds, bool onlyTopCheapest = false)
        {
            var suggestions = new List<string>();
            var warnings = new List<string>();
            decimal originalBudget = model.Budget;

            if (originalBudget <= 0)
                return new List<string> { "⚠️ Vui lòng nhập ngân sách hợp lệ." };

            if (string.IsNullOrEmpty(model.StartLocation))
                return new List<string> { "⚠️ Vui lòng nhập vị trí bắt đầu." };

            int recommendedDays = CalculateRecommendedDays(selectedPlaceIds.Count);
            int actualDays = model.NumberOfDays > 0 ? model.NumberOfDays : recommendedDays;

            // Cảnh báo khi số ngày quá nhiều so với địa điểm
            if (actualDays > selectedPlaceIds.Count * 2)
            {
                warnings.Add($"⚠️ Gợi ý: {selectedPlaceIds.Count} địa điểm với {actualDays} ngày có thể dư thừa. " +
                            $"Bạn sẽ có nhiều thời gian nghỉ ngơi và khám phá sâu hơn mỗi địa điểm.");
            }
            else if (actualDays > recommendedDays + 2)
            {
                warnings.Add($"⚠️ Gợi ý: {selectedPlaceIds.Count} địa điểm nên đi {recommendedDays}-{recommendedDays + 1} ngày thay vì {actualDays} ngày để tối ưu chi phí.");
            }

            double distanceKm = model.DistanceKm > 0 ? model.DistanceKm : 1;
            bool isFromOtherCity = !(model.StartLocation?.Contains("Đà Lạt") ?? false);

            if (!ValidateSelectedPlaces(selectedPlaceIds))
            {
                warnings.Add("• Bạn chưa chọn địa điểm cụ thể — hệ thống sẽ lập kế hoạch tổng quát cho Đà Lạt dựa trên ngân sách và số ngày.");
            }

            // Tìm location legacy
            var legacyLocation = _context.LegacyLocations
                .AsEnumerable()
                .FirstOrDefault(l => !string.IsNullOrEmpty(l.OldName) &&
                                     !string.IsNullOrEmpty(model.StartLocation) &&
                                     model.StartLocation.ToLower().Contains(l.OldName.ToLower()));

            // Lấy danh sách phương tiện phù hợp
            var mainTransports = model.TransportOptions
                .Where(t => isFromOtherCity ? !t.Name.Contains("Taxi nội thành")
                                            : t.Name.Contains("Taxi nội thành") || t.IsSelfDrive)
                .ToList();

            // Áp dụng bộ lọc phương tiện nếu người dùng đã chọn
            if (model.SelectedTransportId.HasValue)
            {
                mainTransports = mainTransports
                    .Where(t => t.Id == model.SelectedTransportId.Value)
                    .ToList();
            }

            if (!mainTransports.Any())
                return new List<string> { "Không có phương tiện phù hợp với vị trí xuất phát." };

            // Lặp qua từng phương tiện để tính toán
            foreach (var transport in mainTransports)
            {
                try
                {
                    // 1. Tính chi phí vận chuyển chính
                    decimal transportCost = CalculateTransportCost(transport, legacyLocation, distanceKm,
                        selectedPlaceIds, actualDays, isFromOtherCity);

                    // 2. Lấy danh sách địa điểm đã chọn
                    var selectedPlaces = _context.TouristPlaces
                        .Where(p => selectedPlaceIds.Contains(p.Id))
                        .ToList();

                    // 3. Tạo lịch trình tối ưu
                    double startLat = 11.940419;  // Tạm tọa độ trung tâm Đà Lạt
                    double startLng = 108.458313;
                    var route = OptimizeRoute(startLat, startLng, selectedPlaces);

                    // 4. Tính toán ngân sách còn lại sau chi phí vận chuyển
                    decimal remainingBudget = originalBudget - transportCost;
                    if (remainingBudget <= 0) continue;

                    // 5. Sử dụng logic mới để tính chi phí khách sạn
                    var hotelCalculation = CalculateOptimizedHotelCosts(selectedPlaceIds, remainingBudget * 0.35m, actualDays);
                    decimal hotelCost = hotelCalculation.TotalCost;
                    string hotelDetails = hotelCalculation.Details;
                    var selectedHotels = hotelCalculation.SelectedHotels;

                    // 6. Cập nhật ngân sách còn lại
                    remainingBudget -= hotelCost;
                    if (remainingBudget <= 0) continue;

                    // 7. Sử dụng logic mới để tính chi phí ăn uống
                    var foodCalculation = CalculateOptimizedFoodCosts(selectedPlaceIds, remainingBudget * 0.6m, actualDays);
                    decimal foodCost = foodCalculation.TotalCost;
                    string foodDetails = foodCalculation.Details;

                    // 8. Tính chi phí vé tham quan
                    var ticketCalculation = CalculateTicketCosts(selectedPlaceIds, model);
                    decimal ticketCost = ticketCalculation.TotalCost;
                    var ticketDetails = ticketCalculation.TicketDetails;
                    var ticketWarnings = ticketCalculation.Warnings;

                    // 9. Tính chi phí di chuyển nội thành (sử dụng khách sạn đầu tiên làm base)
                    var primaryHotel = selectedHotels.FirstOrDefault() ?? _context.Hotels.First();
                    var localTransportCalculation = CalculateLocalTransportCosts(
                        selectedPlaceIds, actualDays, primaryHotel, transport);

                    decimal localTransportCost = localTransportCalculation.TotalCost;
                    var localTransportDetails = localTransportCalculation.Details;

                    // 10. Tính chi phí phát sinh
                    decimal miscCost = Math.Round((transportCost + hotelCost + foodCost + ticketCost + localTransportCost) * 0.1m, 0);
                    decimal totalCost = transportCost + hotelCost + foodCost + ticketCost + localTransportCost + miscCost;

                    // 11. Kiểm tra ngân sách
                    if (totalCost > originalBudget) continue;

                    decimal remaining = originalBudget - totalCost;

                    // 12. Tạo chi tiết lịch trình
                    string routeDetails = string.Join("<br/>", route.Select((p, idx) => $"{idx + 1}. {p.Name}"));

                    // 13. Tạo cluster details nếu có nhiều khách sạn
                    string clusterDetails = "";
                    if (selectedHotels.Count > 1)
                    {
                        var places = _context.TouristPlaces.Where(p => selectedPlaceIds.Contains(p.Id)).ToList();
                        var clusters = ClusterPlacesByDistance(places, actualDays);

                        clusterDetails = "<br/><b>📍 Phân bổ thời gian:</b><br/>";
                        for (int i = 0; i < clusters.Count; i++)
                        {
                            var cluster = clusters[i];
                            clusterDetails += $"Giai đoạn {i + 1}: {cluster.RecommendedNights + 1} ngày tại " +
                                            $"{string.Join(", ", cluster.Places.Take(2).Select(p => p.Name))}" +
                                            $"{(cluster.Places.Count > 2 ? "..." : "")}<br/>";
                        }
                    }

                    // 13.1 Nếu không chọn địa điểm: thêm đích đến Đà Lạt và gợi ý địa điểm để người dùng chọn
                    if (selectedPlaceIds == null || !selectedPlaceIds.Any())
                    {
                        var suggested = SuggestDefaultPlaces(6);
                        if (suggested.Any())
                        {
                            var suggestionsList = string.Join("<br/>", suggested.Select(p => $"• {p.Name}"));
                            clusterDetails = $"<br/><b>📍 Đích đến: Đà Lạt</b><br/><b>📌 Gợi ý địa điểm để chọn:</b><br/>{suggestionsList}";
                        }
                        else
                        {
                            clusterDetails = $"<br/><b>📍 Đích đến: Đà Lạt</b>";
                        }
                    }

                    // 13.2 Nếu không chọn địa điểm: chỉ hiển thị chi phí đến Đà Lạt và gợi ý địa điểm
                    bool basicOnly = selectedPlaceIds == null || !selectedPlaceIds.Any();
                    if (basicOnly)
                    {
                        // Ẩn toàn bộ các chi phí khác, chỉ giữ chi phí đến Đà Lạt
                        hotelCost = 0; foodCost = 0; ticketCost = 0; localTransportCost = 0; miscCost = 0;
                        totalCost = transportCost;
                        remaining = originalBudget - totalCost;
                        routeDetails = string.Empty;
                        ticketDetails = new List<string>();
                        localTransportDetails = new List<string>();
                        ticketWarnings = new List<string>();
                        warnings.Clear();
                    }

                    // 14. Format suggestion
                    string suggestion = FormatOptimizedSuggestion(
                        transport, transportCost,
                        hotelDetails, hotelCost,
                        foodDetails, foodCost,
                        ticketCost, localTransportCost, miscCost,
                        totalCost, remaining, actualDays,
                        ticketDetails, localTransportDetails, ticketWarnings,
                        routeDetails, clusterDetails, basicOnly);

                    suggestions.Add(suggestion);
                }
                catch (Exception ex)
                {
                    suggestions.Add($"⚠️ Lỗi tính toán cho phương tiện {transport.Name}: {ex.Message}");
                }
            }

            // 15. Xử lý kết quả
            if (!suggestions.Any())
                return GenerateBudgetWarning(originalBudget, actualDays, selectedPlaceIds.Count);

            // 16. Loại bỏ duplicate và sắp xếp
            suggestions = RemoveDuplicateSuggestions(suggestions)
                         .OrderBy(s => ExtractTotalCost(s))
                         .Take(5).ToList();

            // 17. Thêm warnings nếu có
            if (warnings.Any())
            {
                var finalResults = new List<string>();
                finalResults.AddRange(warnings);
                finalResults.AddRange(suggestions);
                return finalResults;
            }

            return suggestions;
        }


        // NEW: Calculate local transport costs within Da Lat with advanced logic

        private (decimal TotalCost, List<string> Details) CalculateLocalTransportCosts(
    List<string> selectedPlaceIds,
    int days,
    Hotel selectedHotel,
    TransportOption selectedTransport = null)
        {
            var details = new List<string>();
            decimal totalCost = 0;

            // Validate inputs
            if (selectedPlaceIds == null || !selectedPlaceIds.Any())
            {
                details.Add("• Không có địa điểm để di chuyển");
                return (50000 * days, details);
            }

            if (days <= 0)
            {
                details.Add("• Số ngày không hợp lệ");
                return (0, details);
            }

            // Get tourist places
            var places = _context.TouristPlaces
                .Where(p => selectedPlaceIds.Contains(p.Id))
                .ToList();

            if (!places.Any())
            {
                decimal defaultCost = 50000 * days;
                details.Add($"• Di chuyển nội thành (ước tính): {FormatCurrency(defaultCost)}");
                return (defaultCost, details);
            }

            // Hotel coordinates
            double startLat = selectedHotel?.Latitude ?? 11.940419;
            double startLng = selectedHotel?.Longitude ?? 108.458313;

            try
            {
                // SỬA: Gọi hàm với tham số days chính xác
                if (selectedTransport != null && selectedTransport.IsSelfDrive)
                {
                    return CalculatePersonalVehicleTransportWithFullSchedule(places, days, startLat, startLng, selectedTransport, details);
                }

                return CalculateTaxiTransportWithFullSchedule(places, days, startLat, startLng, details);
            }
            catch (Exception ex)
            {
                details.Add($"• Lỗi tính toán di chuyển: {ex.Message}");
                decimal fallbackCost = days * 100000;
                return (fallbackCost, details);
            }
        }

        // Helper methods cho CalculateLocalTransportCosts (giữ nguyên như đã viết trước đó)
        private (decimal TotalCost, List<string> Details) CalculatePersonalVehicleTransport(
            List<TouristPlace> places,
            int days,
            double startLat,
            double startLng,
            TransportOption transport,
            List<string> details)
        {
            decimal totalCost = 0;
            double totalDistance = 0;
            var allDayRoutes = new List<string>();

            // Distribute places across days
            var dailyPlaces = DistributePlacesAcrossDays(places, days);

            for (int day = 1; day <= days; day++)
            {
                var dayPlaces = dailyPlaces.ElementAtOrDefault(day - 1) ?? new List<TouristPlace>();

                if (!dayPlaces.Any())
                {
                    allDayRoutes.Add($"Ngày {day}: Nghỉ ngơi/khám phá khu vực (~5 km)");
                    totalDistance += 5;
                    continue;
                }

                var (dayDistance, routeDescription) = CalculateDayRoute(dayPlaces, startLat, startLng, day);
                totalDistance += dayDistance;
                allDayRoutes.Add(routeDescription);
            }

            // Calculate fuel cost
            if (transport.FuelConsumption > 0 && transport.FuelPrice > 0)
            {
                decimal fuelUsed = ((decimal)totalDistance * transport.FuelConsumption) / 100m;
                totalCost = fuelUsed * transport.FuelPrice;

                details.Add($"• {transport.Name} (phương tiện cá nhân)");
                details.AddRange(allDayRoutes);
                details.Add($"↳ Tổng quãng đường: ~{totalDistance:F1} km");
                details.Add($"↳ Nhiên liệu: {fuelUsed:F2} lít × {transport.FuelPrice:N0}đ = {FormatCurrency(totalCost)}");
            }
            else
            {
                totalCost = (decimal)totalDistance * 3000;
                details.Add($"• {transport.Name} (ước tính)");
                details.AddRange(allDayRoutes);
                details.Add($"↳ Chi phí ước tính: {FormatCurrency(totalCost)}");
            }

            return (totalCost, details);
        }

        private (decimal TotalCost, List<string> Details) CalculateTaxiTransport(
            List<TouristPlace> places,
            int days,
            double startLat,
            double startLng,
            List<string> details)
        {
            decimal totalCost = 0;
            double totalDistance = 0;
            var allDayRoutes = new List<string>();
            decimal taxiRatePerKm = 15000;

            var dailyPlaces = DistributePlacesAcrossDays(places, days);

            for (int day = 1; day <= days; day++)
            {
                var dayPlaces = dailyPlaces.ElementAtOrDefault(day - 1) ?? new List<TouristPlace>();

                if (!dayPlaces.Any())
                {
                    allDayRoutes.Add($"Ngày {day}: Nghỉ ngơi (~0 km)");
                    continue;
                }

                var (dayDistance, routeDescription) = CalculateDayRoute(dayPlaces, startLat, startLng, day);
                totalDistance += dayDistance;
                allDayRoutes.Add(routeDescription);
            }

            totalCost = (decimal)totalDistance * taxiRatePerKm;

            details.Add("• Taxi nội thành");
            details.AddRange(allDayRoutes);
            details.Add($"↳ Tổng quãng đường: {totalDistance:F1} km × {taxiRatePerKm:N0}đ/km = {FormatCurrency(totalCost)}");

            return (totalCost, details);
        }

        private (double Distance, string RouteDescription) CalculateDayRoute(
            List<TouristPlace> dayPlaces,
            double startLat,
            double startLng,
            int dayNumber)
        {
            if (!dayPlaces.Any())
                return (0, $"Ngày {dayNumber}: Nghỉ ngơi");

            double totalDayDistance = 0;
            var routeParts = new List<string> { "Khách sạn" };

            double currentLat = startLat;
            double currentLng = startLng;

            foreach (var place in dayPlaces)
            {
                try
                {
                    double distanceToPlace = GetDistance(currentLat, currentLng, place.Latitude, place.Longitude);
                    totalDayDistance += distanceToPlace;
                    routeParts.Add($"{place.Name} (~{distanceToPlace:F1} km)");

                    currentLat = place.Latitude;
                    currentLng = place.Longitude;
                }
                catch (Exception)
                {
                    routeParts.Add($"{place.Name} (lỗi tọa độ)");
                    totalDayDistance += 5;
                }
            }

            // Return to hotel
            try
            {
                double returnDistance = GetDistance(currentLat, currentLng, startLat, startLng);
                totalDayDistance += returnDistance;
                routeParts.Add($"Khách sạn (~{returnDistance:F1} km)");
            }
            catch (Exception)
            {
                totalDayDistance += 5;
                routeParts.Add("Khách sạn (~5 km)");
            }

            string routeDescription = $"Ngày {dayNumber}: {string.Join(" → ", routeParts)} | Tổng: ~{totalDayDistance:F1} km";
            return (totalDayDistance, routeDescription);
        }






        // ============= TÍNH QUÃNG ĐƯỜNG CHIA NGÀY + KHỨ HỒI ============= 




        private (decimal TotalCost, List<string> Details) CalculatePersonalVehicleCost(
    List<string> selectedPlaceIds,
    int days,
    TransportOption selectedTransport)
        {
            var details = new List<string>();

            // Ước lượng khoảng cách đi trong ngày
            decimal totalDistance = 0;
            foreach (var placeId in selectedPlaceIds)
                totalDistance += GetEstimatedDistance(placeId);

            // Nếu nhiều điểm/ngày thì thêm quãng đường giữa các điểm
            if (selectedPlaceIds.Count > 1)
                totalDistance += (selectedPlaceIds.Count - 1) * 4;

            // Quay về khách sạn
            totalDistance += GetEstimatedDistance(selectedPlaceIds.Last());

            // Nhân số ngày
            totalDistance *= days;

            // Chi phí nhiên liệu
            decimal fuelUsed = (selectedTransport.FuelConsumption / 100m) * totalDistance;
            decimal fuelCost = fuelUsed * selectedTransport.FuelPrice;

            // Hao mòn + bảo dưỡng (20%)
            decimal maintenance = fuelCost * 0.2m;

            decimal totalCost = fuelCost + maintenance;
            details.Add($"• {selectedTransport.Name}: {FormatCurrency(totalCost)} (~{totalDistance:F0}km, {fuelUsed:F2} lít)");

            return (totalCost, details);
        }
        private (decimal Cost, List<string> Details) CalculateElectricShuttleStrategy(List<string> selectedPlaceIds, List<LocalTransport> localTransports, bool hasMultiplePlacesPerDay)
        {
            var details = new List<string>();
            decimal totalCost = 0;
            var electricPlaces = new List<string>();
            var nonElectricPlaces = new List<string>();

            // Separate places with electric shuttle vs without
            foreach (var placeId in selectedPlaceIds)
            {
                var electricShuttle = localTransports.FirstOrDefault(lt =>
                    lt.TransportType == TransportType.ElectricShuttle &&
                    (lt.TouristPlaceId == placeId || lt.TouristPlaceId == null));

                if (electricShuttle != null)
                {
                    electricPlaces.Add(placeId);
                    decimal cost = (electricShuttle.PricePerTrip ?? 50000) * 2; // Round trip
                    totalCost += cost;

                    var place = _context.TouristPlaces.FirstOrDefault(p => p.Id == placeId);
                    details.Add($"• {place?.Name}: Xe điện {FormatCurrency(cost)} (khứ hồi)");
                }
                else
                {
                    nonElectricPlaces.Add(placeId);
                }
            }

            // Add taxi cost for non-electric places
            if (nonElectricPlaces.Any())
            {
                var taxiTransport = localTransports.FirstOrDefault(lt => lt.TransportType == TransportType.LocalTaxi);
                decimal taxiCostPerKm = taxiTransport?.PricePerKm ?? 15000;

                foreach (var placeId in nonElectricPlaces)
                {
                    decimal estimatedKm = GetEstimatedDistance(placeId);
                    decimal tripCost = taxiCostPerKm * estimatedKm * 2; // Round trip

                    // If multiple places in a day, add inter-destination cost
                    if (hasMultiplePlacesPerDay)
                        tripCost += taxiCostPerKm * 3; // Average 3km between destinations

                    totalCost += tripCost;

                    var place = _context.TouristPlaces.FirstOrDefault(p => p.Id == placeId);
                    details.Add($"• {place?.Name}: Taxi {FormatCurrency(tripCost)} ({estimatedKm * 2}km)");
                }
            }

            return (totalCost, details);
        }
        private (decimal Cost, List<string> Details) CalculateTaxiStrategy(List<string> selectedPlaceIds, List<LocalTransport> localTransports, Hotel selectedHotel, bool hasMultiplePlacesPerDay)
        {
            var details = new List<string>();
            var taxiTransport = localTransports.FirstOrDefault(lt => lt.TransportType == TransportType.LocalTaxi);
            decimal taxiCostPerKm = taxiTransport?.PricePerKm ?? 15000;
            decimal totalCost = 0;

            if (hasMultiplePlacesPerDay)
            {
                // Calculate optimized route cost
                decimal totalDistance = 0;

                foreach (var placeId in selectedPlaceIds)
                {
                    totalDistance += GetEstimatedDistance(placeId);
                }

                // Add inter-destination distances
                if (selectedPlaceIds.Count > 1)
                {
                    totalDistance += (selectedPlaceIds.Count - 1) * 4; // Average 4km between places
                }

                // Add return to hotel
                totalDistance += GetEstimatedDistance(selectedPlaceIds.Last());

                totalCost = totalDistance * taxiCostPerKm;
                details.Add($"• Taxi cho tất cả điểm: {FormatCurrency(totalCost)} (~{totalDistance:F0}km)");
                details.Add($"  ↳ Bao gồm: di chuyển giữa các điểm + về khách sạn");
            }
            else
            {
                // Simple round trip for each place
                foreach (var placeId in selectedPlaceIds)
                {
                    decimal estimatedKm = GetEstimatedDistance(placeId);
                    decimal tripCost = taxiCostPerKm * estimatedKm * 2; // Round trip
                    totalCost += tripCost;

                    var place = _context.TouristPlaces.FirstOrDefault(p => p.Id == placeId);
                    details.Add($"• {place?.Name}: {FormatCurrency(tripCost)} ({estimatedKm * 2}km khứ hồi)");
                }
            }

            return (totalCost, details);
        }

        private (decimal Cost, List<string> Details) CalculateHotelShuttleStrategy(List<string> selectedPlaceIds, List<LocalTransport> localTransports, Hotel selectedHotel, bool hasMultiplePlacesPerDay)
        {
            var details = new List<string>();
            decimal totalCost = 0;
            var shuttlePlaces = new List<string>();
            var nonShuttlePlaces = new List<string>();

            // Check which places have hotel shuttle service
            foreach (var placeId in selectedPlaceIds)
            {
                var hotelShuttle = localTransports.FirstOrDefault(lt =>
                    lt.TransportType == TransportType.HotelShuttle &&
                    (lt.HotelId == selectedHotel.Id || lt.HotelId == null) &&
                    (lt.TouristPlaceId == placeId || lt.TouristPlaceId == null));

                if (hotelShuttle != null)
                {
                    shuttlePlaces.Add(placeId);
                    decimal cost = (hotelShuttle.PricePerTrip ?? 0) * 2; // Round trip
                    totalCost += cost;

                    var place = _context.TouristPlaces.FirstOrDefault(p => p.Id == placeId);
                    details.Add($"• {place?.Name}: Xe buýt KS {FormatCurrency(cost)} (khứ hồi)");
                }
                else
                {
                    nonShuttlePlaces.Add(placeId);
                }
            }

            // Add taxi for places without shuttle
            if (nonShuttlePlaces.Any())
            {
                var taxiStrategy = CalculateTaxiStrategy(nonShuttlePlaces, localTransports, selectedHotel, hasMultiplePlacesPerDay && nonShuttlePlaces.Count > 1);
                totalCost += taxiStrategy.Cost;
                details.AddRange(taxiStrategy.Details);
            }

            return shuttlePlaces.Any() ? (totalCost, details) : (0, new List<string>());
        }

        private (decimal Cost, List<string> Details) CalculateMotorbikeStrategy(List<LocalTransport> localTransports, int days)
        {
            var details = new List<string>();
            var motorbike = localTransports.Where(lt => lt.TransportType == TransportType.MotorbikeRental)
                                         .OrderBy(lt => lt.PricePerDay ?? decimal.MaxValue)
                                         .FirstOrDefault();

            if (motorbike == null)
                return (0, details);

            decimal dailyCost = motorbike.PricePerDay ?? 150000;
            decimal totalCost = dailyCost * days;

            details.Add($"• {motorbike.Name}: {FormatCurrency(totalCost)} ({days} ngày × {FormatCurrency(dailyCost)})");
            details.Add($"  ↳ Tự do di chuyển, phù hợp nhiều điểm/ngày");

            return (totalCost, details);
        }

        // Helper method to estimate distance from hotel to tourist place
        private decimal GetEstimatedDistance(string placeId)
        {
            // This could be enhanced with actual GPS coordinates calculation
            // For now, use estimated distances based on place type or location
            var place = _context.TouristPlaces.FirstOrDefault(p => p.Id == placeId);

            // Default distances (can be customized based on actual locations)
            return place?.Name?.ToLower() switch
            {
                var name when name.Contains("hồ xuân hương") => 2,
                var name when name.Contains("langbiang") => 12,
                var name when name.Contains("valley") => 8,
                var name when name.Contains("dalat flower") => 6,
                var name when name.Contains("crazy house") => 3,
                var name when name.Contains("bao dai") => 4,
                var name when name.Contains("linh phuoc") => 15,
                var name when name.Contains("elephant") => 8,
                _ => 5 // Default 5km
            };
        }

        // Updated method signature to include local transport
        private (decimal TotalCost, string Details, List<Hotel> SelectedHotels) CalculateOptimizedHotelCosts(
    List<string> selectedPlaceIds,
    decimal hotelBudget,
    int days)
{
    int nights = days > 1 ? days - 1 : 0;
    if (nights == 0) 
        return (0, "Chuyến đi trong ngày - không cần nghỉ đêm", new List<Hotel>());

    if (hotelBudget <= 0)
        return (0, "Không có ngân sách cho khách sạn", new List<Hotel>());

    var places = _context.TouristPlaces
        .Where(p => selectedPlaceIds.Contains(p.Id))
        .ToList();

    // Fallback: không chọn địa điểm -> đề xuất khách sạn theo ngân sách toàn thành phố
    if (!places.Any())
    {
        var details = new List<string>();
        var selectedHotels = new List<Hotel>();
        decimal totalCost = 0;
        decimal budgetPerNight = hotelBudget / nights;

        // Chọn tối đa 1-2 khách sạn phù hợp giá để minh họa (ưu tiên gần ngân sách/đêm)
        var candidates = _context.Hotels
            .OrderBy(h => Math.Abs(h.PricePerNight - budgetPerNight))
            .ThenBy(h => h.PricePerNight)
            .Take(2)
            .ToList();

        if (!candidates.Any())
            return (0, "Không tìm thấy khách sạn phù hợp", new List<Hotel>());

        foreach (var hotel in candidates)
        {
            int nightsForHotel = Math.Max(1, nights / candidates.Count);
            decimal clusterCost = hotel.PricePerNight * nightsForHotel;
            if (totalCost + clusterCost > hotelBudget)
            {
                // điều chỉnh để không vượt ngân sách
                nightsForHotel = (int)Math.Floor((hotelBudget - totalCost) / Math.Max(1, hotel.PricePerNight));
                if (nightsForHotel <= 0) break;
                clusterCost = hotel.PricePerNight * nightsForHotel;
            }

            selectedHotels.Add(hotel);
            totalCost += clusterCost;
            details.Add($"• {hotel.Name}: {nightsForHotel} đêm × {FormatCurrency(hotel.PricePerNight)}");

            if (totalCost >= hotelBudget) break;
        }

        return (totalCost, string.Join("<br/>", details), selectedHotels);
    }

    var clusters = ClusterPlacesByDistance(places, days);
    
    if (!clusters.Any())
        return (0, "Không thể tạo cụm địa điểm", new List<Hotel>());

    var selectedHotels2 = new List<Hotel>();
    var details2 = new List<string>();
    decimal totalCost2 = 0;
    decimal budgetPerNight2 = hotelBudget / nights;

    foreach (var cluster in clusters)
    {
        int nightsForCluster = cluster.RecommendedNights;
        
        if (nightsForCluster <= 0)
            continue;

        decimal budgetForCluster = budgetPerNight2 * nightsForCluster;
        decimal maxPricePerNight = budgetForCluster / nightsForCluster;

        var hotel = FindBestHotelForCluster(cluster.Places, maxPricePerNight);
        
        if (hotel != null)
        {
            selectedHotels2.Add(hotel);
            decimal clusterCost = hotel.PricePerNight * nightsForCluster;
            totalCost2 += clusterCost;

            // SỬA: HIỂN THỊ ĐẦY ĐỦ TÊN CÁC ĐỊA ĐIỂM
            string locationNames;
            if (cluster.Places.Count <= 3)
            {
                // Hiển thị tất cả nếu <= 3 địa điểm
                locationNames = string.Join(", ", cluster.Places.Select(p => p.Name));
            }
            else
            {
                // Hiển thị 3 địa điểm đầu + số còn lại
                var firstThree = string.Join(", ", cluster.Places.Take(3).Select(p => p.Name));
                locationNames = $"{firstThree} và {cluster.Places.Count - 3} địa điểm khác";
            }

            details2.Add($"• {hotel.Name} (Khu vực: {locationNames}): " +
                       $"{nightsForCluster} đêm × {FormatCurrency(hotel.PricePerNight)}");
        }
    }

    if (!selectedHotels2.Any())
    {
        var defaultHotel = _context.Hotels.OrderBy(h => h.PricePerNight).FirstOrDefault();
        if (defaultHotel != null)
        {
            selectedHotels2.Add(defaultHotel);
            decimal defaultCost = Math.Min(defaultHotel.PricePerNight * nights, hotelBudget);
            totalCost2 = defaultCost;
            details2.Add($"• {defaultHotel.Name} (Mặc định): {nights} đêm × {FormatCurrency(defaultCost / nights)}");
        }
    }

    return (totalCost2, string.Join("<br/>", details2), selectedHotels2);
}



        // CHƯA SỬ DỤNG DO HOTEL SỬ DỤNG CỤM TỌA ĐỘ
        /*private Hotel FindBestHotelForCluster(List<TouristPlace> cluster, decimal budgetPerDay)
        {
            if (cluster == null || !cluster.Any())
                return null;

            // 1. Tính trung tâm cụm điểm đến
            double centerLat = cluster.Average(p => p.Latitude);
            double centerLng = cluster.Average(p => p.Longitude);

            // 2. Lấy tất cả khách sạn (hoặc lọc sơ bộ theo phân khúc)
            var hotels = GetHotelsByBudgetSegment(
                cluster.Select(p => p.Id).ToList(),
                budgetPerDay
            );

            if (!hotels.Any())
                return null;

            // 3. Tìm khách sạn gần trung tâm nhất
            var bestHotel = hotels
                .OrderBy(h => GetDistance(h.Latitude, h.Longitude, centerLat, centerLng))
                .FirstOrDefault();

            return bestHotel;
        }*/



        // Hàm tính khoảng cách (Haversine)
        private double GetDistance(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371; // bán kính Trái Đất (km)
            double dLat = ToRadians(lat2 - lat1);
            double dLon = ToRadians(lon2 - lon1);
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private double ToRadians(double angle) => angle * Math.PI / 180;
        // Updated FormatSuggestion to include local transport
        private string FormatSuggestion(TransportOption transport, decimal transportCost,
            string hotelDetails, decimal hotelCost, string foodDetails, decimal foodCost,
            decimal ticketCost, decimal localTransportCost, decimal miscCost, decimal totalCost,
            decimal remaining, int days, List<string> ticketDetails, List<string> localTransportDetails,
            List<string> warnings)
        {
            var sb = new StringBuilder();
            sb.Append($"🚗 <strong>{transport.Name}</strong> ({transport.Type}): {FormatCurrency(transportCost)}<br/>");
            sb.Append($"🏨 {hotelDetails}<br/>");
            sb.Append($"🍽️ {foodDetails}<br/>");
            sb.Append($"🎫 Vé tham quan: {FormatCurrency(ticketCost)}<br/>");

            // NEW: Add local transport details
            if (localTransportCost > 0)
            {
                sb.Append($"🚌 Di chuyển nội thành: {FormatCurrency(localTransportCost)}<br/>");
                if (localTransportDetails.Any())
                {
                    foreach (var detail in localTransportDetails)
                    {
                        sb.Append($"  {detail}<br/>");
                    }
                }
            }

            sb.Append($"💡 Chi phí phát sinh (10%): {FormatCurrency(miscCost)}<br/>");
            sb.Append($"💰 <strong>Tổng chi phí: {FormatCurrency(totalCost)} | Còn lại: {FormatCurrency(remaining)}</strong><br/>");

            if (ticketDetails.Any())
            {
                sb.Append($"📍 <strong>Chi tiết {days} ngày:</strong><br/>");
                foreach (var d in ticketDetails) sb.Append($"{d}<br/>");
            }

            if (warnings.Any())
            {
                sb.Append($"⚠️ <strong>Lưu ý:</strong><br/>");
                foreach (var w in warnings) sb.Append($"{w}<br/>");
            }

            var content = sb.ToString();
            return $"<div class='suggestion' data-transport='{transportCost}' data-hotel='{hotelCost}' data-food='{foodCost}' data-ticket='{ticketCost}' data-local='{localTransportCost}' data-misc='{miscCost}' data-total='{totalCost}'>{content}</div>";
        }

        // Rest of the existing methods remain the same...
        private List<string> RemoveDuplicateSuggestions(List<string> suggestions)
        {
            var uniqueSuggestions = new List<string>();
            var seenTransports = new HashSet<string>();

            foreach (var suggestion in suggestions)
            {
                var match = Regex.Match(suggestion, @"🚗 <strong>([^<]+)</strong>");
                if (match.Success)
                {
                    string transportName = match.Groups[1].Value;
                    if (!seenTransports.Contains(transportName))
                    {
                        seenTransports.Add(transportName);
                        uniqueSuggestions.Add(suggestion);
                    }
                }
            }

            return uniqueSuggestions;
        }

        private int CalculateRecommendedDays(int numberOfPlaces) =>
            numberOfPlaces switch
            {
                <= 0 => 1,
                <= 2 => 1,
                <= 4 => 2,
                <= 6 => 3,
                <= 8 => 4,
                _ => 5
            };

        private decimal ExtractTotalCost(string suggestion)
        {
            var match = Regex.Match(suggestion, @"Tổng chi phí:\s([\d,.]+)đ");
            if (match.Success)
            {
                var value = match.Groups[1].Value.Replace(".", "").Replace(",", "");
                return decimal.TryParse(value, out var cost) ? cost : decimal.MaxValue;
            }
            return decimal.MaxValue;
        }

        private (decimal TotalCost, string Details) CalculateOptimizedFoodCosts(
     List<string> selectedPlaceIds,
     decimal foodBudget,
     int days)
        {
            if (days <= 0 || foodBudget <= 0)
                return (0, "Không có ngân sách ăn uống");

            var places = _context.TouristPlaces
                .Where(p => selectedPlaceIds.Contains(p.Id))
                .ToList();

            if (!places.Any())
                return (foodBudget, $"Ăn uống tổng quát: {days} ngày × {FormatCurrency(foodBudget / days)}/ngày");

            // Tính số ngày thực tế tại mỗi khu vực
            var clusters = ClusterPlacesByDistance(places, days);
            var details = new List<string>();
            decimal totalCost = 0;
            decimal dailyBudget = foodBudget / days;

            foreach (var cluster in clusters)
            {
                int daysInCluster = cluster.RecommendedNights + 1; // +1 vì ngày cuối không ngủ lại

                if (daysInCluster <= 0)
                    continue;

                decimal budgetForCluster = dailyBudget * daysInCluster;
                decimal dailyBudgetInCluster = budgetForCluster / daysInCluster;

                // Phân loại theo mức giá
                string segment = dailyBudgetInCluster > 400000 ? "cao cấp"
                                : (dailyBudgetInCluster > 200000 ? "tầm trung" : "tiết kiệm");

                // Chọn nhà hàng phù hợp với khu vực và ngân sách
                var restaurants = GetRestaurantsByLocation(cluster.Places.Select(p => p.Id).ToList(), dailyBudgetInCluster);

                if (restaurants.Any())
                {
                    var selectedRestaurant = restaurants.First();
                    decimal avgMealPrice = Math.Min(selectedRestaurant.AveragePricePerPerson * 2.5m, dailyBudgetInCluster);
                    decimal clusterFoodCost = avgMealPrice * daysInCluster;
                    totalCost += clusterFoodCost;

                    string locationNames = string.Join(", ", cluster.Places.Take(2).Select(p => p.Name));
                    if (cluster.Places.Count > 2)
                        locationNames += "...";

                    details.Add($"• Khu vực {locationNames}: {daysInCluster} ngày × {FormatCurrency(avgMealPrice)}/ngày " +
                               $"tại {selectedRestaurant.Name} ({segment})");
                }
                else
                {
                    decimal clusterFoodCost = dailyBudgetInCluster * daysInCluster;
                    totalCost += clusterFoodCost;

                    string locationNames = string.Join(", ", cluster.Places.Take(2).Select(p => p.Name));
                    details.Add($"• Khu vực {locationNames}: {daysInCluster} ngày × {FormatCurrency(dailyBudgetInCluster)}/ngày ({segment})");
                }
            }

            // Đảm bảo không vượt quá ngân sách
            if (totalCost > foodBudget)
            {
                totalCost = foodBudget;
                details.Clear();
                details.Add($"Ăn uống tổng quát: {days} ngày × {FormatCurrency(foodBudget / days)}/ngày (đã điều chỉnh)");
            }

            return (totalCost, string.Join("<br/>", details));
        }



        private (decimal TotalCost, List<string> TicketDetails, List<string> Warnings) CalculateTicketCosts(
    List<string> selectedPlaceIds,
    TripPlannerViewModel model)
        {
            var details = new List<string>();
            var warnings = new List<string>();
            decimal cost = 0;

            foreach (var placeId in selectedPlaceIds)
            {
                var place = _context.TouristPlaces.FirstOrDefault(p => p.Id == placeId);
                if (place == null) continue;

                var attractions = _context.Attractions
                    .Where(a => a.TouristPlaceId == placeId)
                    .ToList();

                if (!attractions.Any())
                {
                    warnings.Add($"Không có thông tin vé cho địa điểm {place.Name}");
                    continue;
                }

                decimal sum = attractions.Sum(a => a.TicketPrice);
                cost += sum;
                details.Add($"• {place.Name}: Vé tham quan {FormatCurrency(sum)}");
            }

            if (cost == 0)
            {
                warnings.Add("Không tìm thấy thông tin vé tham quan cho các địa điểm đã chọn.");
            }

            return (cost, details, warnings);
        }

        private List<string> GenerateBudgetWarning(decimal budget, int days, int placeCount)
        {
            return new List<string> {
                $"⚠️ <strong>Ngân sách không đủ</strong><br/>" +
                $"Ngân sách hiện tại: {FormatCurrency(budget)} cho {days} ngày và {placeCount} địa điểm.<br/>" +
                $"💡 <strong>Gợi ý:</strong><br/>• Giảm số ngày<br/>• Hoặc tăng ngân sách<br/>• Hoặc giảm địa điểm"
            };
        }

        private bool ValidateSelectedPlaces(List<string> places) => places != null && places.Any();

        private decimal CalculateTransportCost(TransportOption transport, LegacyLocation legacy, double km, List<string> placeIds, int days, bool isOtherCity)
        {
            if (legacy != null)
            {
                var priceHistory = _context.TransportPriceHistories.FirstOrDefault(p => p.LegacyLocationId == legacy.Id && p.TransportOptionId == transport.Id);
                if (priceHistory != null) return priceHistory.Price;
            }

            if (transport.IsSelfDrive)
            {
                double actualDistance = km > 100 ? km : 300;
                if (isOtherCity) actualDistance *= 2;

                decimal fuelCost = (decimal)actualDistance * (transport.FuelConsumption / 100) * transport.FuelPrice;
                decimal maintenanceCost = fuelCost * 0.2m;

                return fuelCost + maintenanceCost;
            }
            else
            {
                return transport.FixedPrice > 0 ? transport.FixedPrice : transport.Price;
            }
        }

        private string FormatCurrency(decimal value) => $"{value:N0}đ";

        private List<Hotel> GetHotelsByBudgetSegment(List<string> placeIds, decimal budgetPerDay)
        {
            // Giới hạn giá khách sạn: 40% ngân sách/ngày
            decimal maxPricePerNight = budgetPerDay * 0.4m;

            var hotels = (placeIds != null && placeIds.Any())
                ? _context.Hotels.Where(h => placeIds.Contains(h.TouristPlaceId)).ToList()
                : _context.Hotels.ToList();

            // Ưu tiên khách sạn trong giới hạn
            var inBudget = hotels.Where(h => h.PricePerNight <= maxPricePerNight).ToList();

            if (inBudget.Any())
            {
                // Ưu tiên khách sạn gần mức 40% nhất (đẹp + hợp lý)
                return inBudget.OrderBy(h => Math.Abs(h.PricePerNight - maxPricePerNight)).ToList();
            }
            else
            {
                // Không có khách sạn trong giới hạn → lấy khách sạn rẻ nhất
                return hotels.OrderBy(h => h.PricePerNight).ToList();
            }
        }

        private List<TouristPlace> OptimizeRoute(double startLat, double startLng, List<TouristPlace> places)
        {
            var route = new List<TouristPlace>();
            var remaining = new List<TouristPlace>(places);

            double currentLat = startLat;
            double currentLng = startLng;

            while (remaining.Any())
            {
                var next = remaining
                    .OrderBy(p => GetDistance(currentLat, currentLng, p.Latitude, p.Longitude))
                    .First();

                route.Add(next);
                remaining.Remove(next);

                currentLat = next.Latitude;
                currentLng = next.Longitude;
            }

            return route;
        }

        private List<Restaurant> GetRestaurantsByBudgetSegment(List<string> placeIds, decimal budgetPerDay)
        {
            // 50% ngân sách ăn uống dành cho mỗi bữa
            decimal targetMealPrice = (budgetPerDay * 0.5m) / 2.5m; // 2.5 bữa/ngày

            var restaurants = (placeIds != null && placeIds.Any())
                ? _context.Restaurants.Where(r => placeIds.Contains(r.TouristPlaceId)).ToList()
                : _context.Restaurants.ToList();

            if (!restaurants.Any())
                return new List<Restaurant>();

            // Sắp xếp theo độ gần với targetMealPrice (càng gần càng ưu tiên)
            return restaurants
                .OrderBy(r => Math.Abs(r.AveragePricePerPerson - targetMealPrice))
                .ToList();
        }

        // Chia địa điểm theo số ngày & tính quãng đường từng ngày (có khứ hồi)
        private double CalculateTotalDistancePerDay(List<TouristPlace> route, double hotelLat, double hotelLng, int days)
        {
            int placesPerDay = (int)Math.Ceiling((double)route.Count / days);
            double totalDistance = 0;

            for (int day = 0; day < days; day++)
            {
                var dayPlaces = route.Skip(day * placesPerDay).Take(placesPerDay).ToList();
                if (!dayPlaces.Any()) continue;

                double dLat = hotelLat;
                double dLng = hotelLng;

                // Đi từng điểm trong ngày
                foreach (var place in dayPlaces)
                {
                    totalDistance += GetDistance(dLat, dLng, place.Latitude, place.Longitude);
                    dLat = place.Latitude;
                    dLng = place.Longitude;
                }

                // Khứ hồi về khách sạn
                totalDistance += GetDistance(dLat, dLng, hotelLat, hotelLng);
            }

            return totalDistance;
        }


        // ============= LOGIC ĐÃ SỬA CÁC LỖI =============

        // 1. SỬA HÀM CLUSTERING - PHÂN CỤM THÔNG MINH HƠN
        // 1. SỬA HÀM CLUSTERING - LOGIC PHÂN BỔ NGÀY THÔNG MINH HƠN
        private List<PlaceCluster> ClusterPlacesByDistance(List<TouristPlace> places, int totalDays)
        {
            if (!places.Any()) return new List<PlaceCluster>();

            var clusters = new List<PlaceCluster>();
            var remaining = new List<TouristPlace>(places);

            // Tính số cluster tối ưu
            int maxClusters = Math.Min(totalDays, places.Count);
            int recommendedClusters;

            // SỬA: LOGIC CHỌN SỐ CLUSTER THÔNG MINH HƠN
            if (totalDays <= 2)
                recommendedClusters = 1;
            else if (totalDays <= 4)
                recommendedClusters = Math.Min(2, maxClusters);
            else if (totalDays <= 6)
                recommendedClusters = Math.Min(3, maxClusters);
            else
                recommendedClusters = Math.Min((totalDays + 2) / 3, maxClusters);

            // Đảm bảo ít nhất 1 cluster
            recommendedClusters = Math.Max(1, recommendedClusters);

            int placesPerCluster = (int)Math.Ceiling((double)places.Count / recommendedClusters);

            // Tạo clusters dựa trên khoảng cách
            for (int c = 0; c < recommendedClusters && remaining.Any(); c++)
            {
                var cluster = new PlaceCluster { Places = new List<TouristPlace>() };

                var centerPlace = remaining.First();
                cluster.Places.Add(centerPlace);
                remaining.Remove(centerPlace);

                var nearbyPlaces = remaining
                    .Where(p => GetDistance(centerPlace.Latitude, centerPlace.Longitude, p.Latitude, p.Longitude) <= 15)
                    .OrderBy(p => GetDistance(centerPlace.Latitude, centerPlace.Longitude, p.Latitude, p.Longitude))
                    .Take(placesPerCluster - 1)
                    .ToList();

                cluster.Places.AddRange(nearbyPlaces);
                remaining.RemoveAll(p => nearbyPlaces.Contains(p));
                clusters.Add(cluster);
            }

            // Phân phối địa điểm còn lại
            while (remaining.Any())
            {
                var place = remaining.First();
                var nearestCluster = clusters
                    .OrderBy(c => c.Places.Min(p => GetDistance(p.Latitude, p.Longitude, place.Latitude, place.Longitude)))
                    .First();

                nearestCluster.Places.Add(place);
                remaining.Remove(place);
            }

            // SỬA: PHÂN BỔ NGÀY ĐỀU HƠN
            if (clusters.Count == 1)
            {
                clusters[0].RecommendedNights = Math.Max(0, totalDays - 1);
            }
            else
            {
                // Phân bổ ngày đều cho các clusters
                int baseDaysPerCluster = totalDays / clusters.Count;
                int extraDays = totalDays % clusters.Count;

                for (int i = 0; i < clusters.Count; i++)
                {
                    int daysForCluster = baseDaysPerCluster + (i < extraDays ? 1 : 0);
                    clusters[i].RecommendedNights = Math.Max(0, daysForCluster - 1);
                }
            }

            return clusters;
        }


        private Hotel FindBestHotelForCluster(List<TouristPlace> clusterPlaces, decimal budgetPerNight)
        {
            if (!clusterPlaces.Any()) return null;

            // Tính trung tâm cụm
            double centerLat = clusterPlaces.Average(p => p.Latitude);
            double centerLng = clusterPlaces.Average(p => p.Longitude);

            // Lấy khách sạn trong khu vực và phù hợp ngân sách
            var placeIds = clusterPlaces.Select(p => p.Id).ToList();
            var candidateHotels = GetHotelsByBudgetSegment(placeIds, budgetPerNight * 2.5m); // Tăng giới hạn

            if (!candidateHotels.Any())
            {
                // Fallback: Lấy tất cả khách sạn gần trung tâm
                candidateHotels = _context.Hotels.ToList();
            }

            // Chọn khách sạn gần trung tâm cụm nhất trong ngân sách
            return candidateHotels
                .Where(h => h.PricePerNight <= budgetPerNight * 1.2m) // Cho phép vượt 20%
                .OrderBy(h => GetDistance(h.Latitude, h.Longitude, centerLat, centerLng))
                .FirstOrDefault()
                ?? candidateHotels.OrderBy(h => h.PricePerNight).First(); // Fallback: rẻ nhất
        }

        private List<Restaurant> GetRestaurantsByLocation(List<string> placeIds, decimal dailyBudget)
        {
            decimal targetMealPrice = (dailyBudget * 0.6m) / 2.5m; // 60% ngân sách cho ăn uống

            var restaurants = _context.Restaurants
                .Where(r => placeIds.Contains(r.TouristPlaceId))
                .OrderBy(r => Math.Abs(r.AveragePricePerPerson - targetMealPrice))
                .ToList();

            // Nếu không có nhà hàng trong khu vực, lấy tất cả
            if (!restaurants.Any())
            {
                restaurants = _context.Restaurants
                    .OrderBy(r => Math.Abs(r.AveragePricePerPerson - targetMealPrice))
                    .ToList();
            }

            return restaurants;
        }
        private string FormatOptimizedSuggestion(TransportOption transport, decimal transportCost,
     string hotelDetails, decimal hotelCost, string foodDetails, decimal foodCost,
     decimal ticketCost, decimal localTransportCost, decimal miscCost, decimal totalCost,
     decimal remaining, int days, List<string> ticketDetails, List<string> localTransportDetails,
     List<string> warnings, string routeDetails, string clusterDetails, bool basicOnly)
        {
            var sb = new StringBuilder();
            sb.Append($"🚗 <strong>{transport.Name}</strong> ({transport.Type}): {FormatCurrency(transportCost)}<br/>");
            if (!basicOnly)
            {
                sb.Append($"🏨 {hotelDetails}<br/>");
                sb.Append($"🍽️ {foodDetails}<br/>");
                sb.Append($"🎫 Vé tham quan: {FormatCurrency(ticketCost)}<br/>");
            }

            if (!basicOnly && localTransportCost > 0)
            {
                sb.Append($"🚌 Di chuyển nội thành: {FormatCurrency(localTransportCost)}<br/>");
                if (localTransportDetails.Any())
                {
                    var fullDetails = ExpandLocalTransportDetails(localTransportDetails, days);
                    foreach (var detail in fullDetails)
                    {
                        sb.Append($"  {detail}<br/>");
                    }
                }
            }

            if (!basicOnly)
            {
                sb.Append($"💡 Chi phí phát sinh (10%): {FormatCurrency(miscCost)}<br/>");
                sb.Append($"💰 <strong>Tổng chi phí: {FormatCurrency(totalCost)} | Còn lại: {FormatCurrency(remaining)}</strong><br/>");
            }

            // Always show the destination and suggestions block if available
            if (!string.IsNullOrEmpty(clusterDetails))
            {
                sb.Append(clusterDetails);
            }

            if (!basicOnly && !string.IsNullOrEmpty(routeDetails))
            {
                sb.Append($"<br/><b>📅 Lịch trình tối ưu:</b><br/>{routeDetails}");
            }

            if (!basicOnly && ticketDetails.Any())
            {
                sb.Append($"<br/><b>🎫 Chi tiết vé tham quan:</b><br/>");
                foreach (var detail in ticketDetails)
                {
                    sb.Append($"{detail}<br/>");
                }
            }

            if (!basicOnly && warnings.Any())
            {
                sb.Append($"<br/><b>⚠️ Lưu ý:</b><br/>");
                foreach (var w in warnings) sb.Append($"{w}<br/>");
            }

            var content = sb.ToString();
            return $"<div class='suggestion' data-transport='{transportCost}' data-hotel='{hotelCost}' data-food='{foodCost}' data-ticket='{ticketCost}' data-local='{localTransportCost}' data-misc='{miscCost}' data-total='{totalCost}'>{content}</div>";
        }
        private List<TouristPlace> SuggestDefaultPlaces(int count = 6)
        {
            return _context.TouristPlaces
                .OrderByDescending(p => p.Rating)
                .ThenBy(p => p.Name)
                .Take(count)
                .ToList();
        }
        private List<List<TouristPlace>> DistributePlacesAcrossDays(List<TouristPlace> places, int days)
        {
            var result = new List<List<TouristPlace>>();

            // SỬA: Khởi tạo chính xác số ngày
            for (int i = 0; i < days; i++)
            {
                result.Add(new List<TouristPlace>());
            }

            if (!places.Any())
            {
                return result; // Trả về danh sách rỗng cho tất cả các ngày
            }

            // SỬA: Logic phân bổ tốt hơn
            if (places.Count <= days)
            {
                // Ít địa điểm hơn số ngày: mỗi địa điểm 1 ngày, ngày còn lại nghỉ
                for (int i = 0; i < places.Count; i++)
                {
                    result[i].Add(places[i]);
                }
                // Các ngày còn lại tự động là rỗng (nghỉ ngơi)
            }
            else
            {
                // Nhiều địa điểm hơn số ngày: phân bổ đều
                int placesPerDay = (int)Math.Ceiling((double)places.Count / days);

                for (int i = 0; i < places.Count; i++)
                {
                    int dayIndex = i / placesPerDay;
                    if (dayIndex >= days) dayIndex = days - 1; // Đảm bảo không vượt quá số ngày
                    result[dayIndex].Add(places[i]);
                }
            }

            return result;
        }

        // SỬA HÀM CalculatePersonalVehicleTransportWithFullSchedule
        private (decimal TotalCost, List<string> Details) CalculatePersonalVehicleTransportWithFullSchedule(
            List<TouristPlace> places,
            int days,
            double startLat,
            double startLng,
            TransportOption transport,
            List<string> details)
        {
            decimal totalCost = 0;
            double totalDistance = 0;
            var allDayRoutes = new List<string>();

            // Phân bổ địa điểm theo ngày
            var dailyPlaces = DistributePlacesAcrossDays(places, days);

            // SỬA: HIỂN THỊ CHÍNH XÁC TẤT CẢ CÁC NGÀY (từ 1 đến days)
            for (int day = 0; day < days; day++) // SỬA: dùng index 0-based
            {
                var dayPlaces = dailyPlaces[day]; // Lấy danh sách địa điểm cho ngày này
                int displayDay = day + 1; // Hiển thị là ngày 1, 2, 3...

                if (!dayPlaces.Any())
                {
                    allDayRoutes.Add($"Ngày {displayDay}: Nghỉ ngơi/tự do khám phá (~5 km)");
                    totalDistance += 5; // Chi phí di chuyển nhỏ cho ngày nghỉ
                }
                else
                {
                    var (dayDistance, routeDescription) = CalculateDayRoute(dayPlaces, startLat, startLng, displayDay);
                    totalDistance += dayDistance;
                    allDayRoutes.Add(routeDescription);
                }
            }

            // Tính chi phí nhiên liệu
            if (transport.FuelConsumption > 0 && transport.FuelPrice > 0)
            {
                decimal fuelUsed = ((decimal)totalDistance * transport.FuelConsumption) / 100m;
                totalCost = fuelUsed * transport.FuelPrice;

                details.Add($"• {transport.Name} (phương tiện cá nhân)");
                details.AddRange(allDayRoutes); // HIỂN THỊ TẤT CẢ NGÀY
                details.Add($"↳ Tổng quãng đường: ~{totalDistance:F1} km");
                details.Add($"↳ Nhiên liệu: {fuelUsed:F2} lít × {transport.FuelPrice:N0}đ = {FormatCurrency(totalCost)}");
            }
            else
            {
                totalCost = (decimal)totalDistance * 3000; // 3,000đ/km ước tính
                details.Add($"• {transport.Name} (ước tính)");
                details.AddRange(allDayRoutes);
                details.Add($"↳ Chi phí ước tính: {FormatCurrency(totalCost)} (~{totalDistance:F1} km)");
            }

            return (totalCost, details);
        }

        // SỬA HÀM CalculateTaxiTransportWithFullSchedule  
        private (decimal TotalCost, List<string> Details) CalculateTaxiTransportWithFullSchedule(
            List<TouristPlace> places,
            int days,
            double startLat,
            double startLng,
            List<string> details)
        {
            decimal totalCost = 0;
            double totalDistance = 0;
            var allDayRoutes = new List<string>();
            decimal taxiRatePerKm = 15000; // 15,000đ/km

            // Phân bổ địa điểm theo ngày
            var dailyPlaces = DistributePlacesAcrossDays(places, days);

            // SỬA: HIỂN THỊ CHÍNH XÁC TẤT CẢ CÁC NGÀY
            for (int day = 0; day < days; day++) // SỬA: dùng index 0-based
            {
                var dayPlaces = dailyPlaces[day]; // Lấy danh sách địa điểm cho ngày này
                int displayDay = day + 1; // Hiển thị là ngày 1, 2, 3...

                if (!dayPlaces.Any())
                {
                    allDayRoutes.Add($"Ngày {displayDay}: Nghỉ ngơi/tự do khám phá (~0 km taxi)");
                    // Không tính chi phí taxi cho ngày nghỉ
                }
                else
                {
                    var (dayDistance, routeDescription) = CalculateDayRoute(dayPlaces, startLat, startLng, displayDay);
                    totalDistance += dayDistance;
                    allDayRoutes.Add(routeDescription);
                }
            }

            // Tính tổng chi phí taxi
            totalCost = (decimal)totalDistance * taxiRatePerKm;

            details.Add("• Taxi nội thành");
            details.AddRange(allDayRoutes); // HIỂN THỊ TẤT CẢ NGÀY
            details.Add($"↳ Tổng quãng đường: {totalDistance:F1} km × {taxiRatePerKm:N0}đ/km = {FormatCurrency(totalCost)}");

            if (totalDistance > 50)
            {
                details.Add($"↳ Lưu ý: Quãng đường dài, có thể thương lượng giá theo ngày");
            }

            return (totalCost, details);
        }

        private List<string> ExpandLocalTransportDetails(List<string> details, int days)
        {
            var result = new List<string>(details ?? new List<string>());
            var existingDays = new HashSet<int>();
            var regex = new Regex("^Ngày\\s+(\\d+)");

            foreach (var line in result)
            {
                var match = regex.Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var n))
                {
                    existingDays.Add(n);
                }
            }

            for (int d = 1; d <= days; d++)
            {
                if (!existingDays.Contains(d))
                {
                    result.Add($"Ngày {d}: Nghỉ ngơi/tự do khám phá (~0 km taxi)");
                }
            }

            // Sắp xếp lại các dòng Ngày X theo thứ tự tăng dần, giữ nguyên các dòng không phải 'Ngày'
            var headerLines = result.Where(l => !regex.IsMatch(l)).ToList();
            var dayLines = result.Where(l => regex.IsMatch(l))
                                 .OrderBy(l => int.Parse(regex.Match(l).Groups[1].Value))
                                 .ToList();

            var merged = new List<string>();
            merged.AddRange(headerLines);
            merged.AddRange(dayLines);
            return merged;
        }

    }

}