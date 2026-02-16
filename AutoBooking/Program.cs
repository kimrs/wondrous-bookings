using AutoBooking;
using Microsoft.Playwright;

var username = Environment.GetEnvironmentVariable("WONDR_USERNAME");
var password = Environment.GetEnvironmentVariable("WONDR_PASSWORD");
var bookingTime = Environment.GetEnvironmentVariable("BOOKING_TIME") ?? "16:00";
var bookingUrl = Environment.GetEnvironmentVariable("BOOKING_URL") ?? "https://playtrening.wondr.cc/schema";
var bookingLocation = Environment.GetEnvironmentVariable("BOOKING_LOCATION") ?? "Play Gamlebyen";

if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("Error: WONDR_USERNAME and WONDR_PASSWORD environment variables are required.");
    return 1;
}

var targetDate = DateTime.Now.AddDays(3);
Console.WriteLine($"Booking class at {bookingTime} on {targetDate:yyyy-MM-dd}");

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = true
});

var page = await browser.NewPageAsync();

try
{
    await BookingService.LoginAsync(page, bookingUrl, username, password);
    Console.WriteLine("Logged in successfully.");

    await BookingService.NavigateToDateAsync(page, bookingUrl, bookingLocation, targetDate);
    Console.WriteLine($"Navigated to {targetDate:yyyy-MM-dd}.");

    await BookingService.BookClassAsync(page, bookingTime, targetDate);
    Console.WriteLine($"Successfully booked class at {bookingTime} on {targetDate:yyyy-MM-dd}!");
    return 0;
}
catch (Exception ex)
{
    // Save debug screenshot and page content on failure
    var debugDir = Path.Combine(AppContext.BaseDirectory, "debug");
    Directory.CreateDirectory(debugDir);
    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    await page.ScreenshotAsync(new PageScreenshotOptions
    {
        Path = Path.Combine(debugDir, $"failure_{timestamp}.png"),
        FullPage = true
    });
    var html = await page.ContentAsync();
    await File.WriteAllTextAsync(Path.Combine(debugDir, $"failure_{timestamp}.html"), html);
    Console.Error.WriteLine($"Debug screenshot and HTML saved to {debugDir}");

    Console.Error.WriteLine($"Booking failed: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
    return 1;
}
