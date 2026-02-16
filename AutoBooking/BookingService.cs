using Microsoft.Playwright;

namespace AutoBooking;

public class BookingService
{
    private const int DefaultTimeoutMs = 30_000;

    public static async Task LoginAsync(IPage page, string bookingUrl, string username, string password)
    {
        // Navigate directly to the login page
        var baseUrl = bookingUrl.Contains("/schema")
            ? bookingUrl.Replace("/schema", "")
            : bookingUrl.TrimEnd('/');
        var loginUrl = $"{baseUrl}/users/login";

        Console.WriteLine($"Navigating to login page: {loginUrl}");
        await page.GotoAsync(loginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Wait for React to render the login form
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForTimeoutAsync(2_000); // Give React time to mount

        // Debug: dump all visible input fields
        var inputs = await page.Locator("input").AllAsync();
        Console.WriteLine($"Found {inputs.Count} input elements on login page:");
        foreach (var input in inputs)
        {
            var name = await input.GetAttributeAsync("name") ?? "(no name)";
            var type = await input.GetAttributeAsync("type") ?? "(no type)";
            var placeholder = await input.GetAttributeAsync("placeholder") ?? "(no placeholder)";
            var id = await input.GetAttributeAsync("id") ?? "(no id)";
            Console.WriteLine($"  input: name={name}, type={type}, placeholder={placeholder}, id={id}");
        }

        // Try multiple selector strategies for the username/email field
        var usernameField = page.Locator("input").Filter(new LocatorFilterOptions
        {
            Has = page.Locator("[name='email'], [name='username'], [name='data[User][email]'], [name='data[User][username]'], [type='email'], [placeholder*='mail'], [placeholder*='bruker']")
        }).First;

        // Fallback: if the filter didn't work, try the first text/email input
        if (!await IsVisibleWithinAsync(usernameField, 3_000))
        {
            Console.WriteLine("Primary selectors failed, trying fallback...");
            usernameField = page.Locator("input[type='email'], input[type='text']").First;
        }

        await usernameField.WaitForAsync(new LocatorWaitForOptions { Timeout = DefaultTimeoutMs });
        Console.WriteLine("Found username field, filling...");
        await usernameField.FillAsync(username);

        var passwordField = page.Locator("input[type='password']").First;
        await passwordField.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
        Console.WriteLine("Found password field, filling...");
        await passwordField.FillAsync(password);

        // Click submit - try multiple selectors
        var submitButton = page.Locator("button[type='submit'], input[type='submit']").First;
        if (!await IsVisibleWithinAsync(submitButton, 3_000))
        {
            submitButton = page.Locator("button").Filter(new LocatorFilterOptions { HasText = "Logg inn" }).First;
        }
        if (!await IsVisibleWithinAsync(submitButton, 2_000))
        {
            submitButton = page.Locator("button").Filter(new LocatorFilterOptions { HasText = "Log in" }).First;
        }

        Console.WriteLine("Clicking submit...");
        await submitButton.ClickAsync();

        // Wait for navigation away from login page
        await page.WaitForURLAsync(url => !url.Contains("/login"), new PageWaitForURLOptions { Timeout = DefaultTimeoutMs });
        Console.WriteLine("Login navigation complete.");
    }

    public static async Task NavigateToDateAsync(IPage page, string bookingUrl, string locationName, DateTime targetDate)
    {
        // Navigate to the schedule page
        var scheduleUrl = $"{bookingUrl.TrimEnd('/')}/index";

        Console.WriteLine($"Navigating to schedule: {scheduleUrl}");
        await page.GotoAsync(scheduleUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForTimeoutAsync(3_000);

        // Select facility via the UI dropdown
        Console.WriteLine($"Selecting facility: {locationName}");
        var facilityButton = page.Locator(".collapse-button, button:has-text('Choose facility'), button:has-text('Velg')").First;
        await facilityButton.WaitForAsync(new LocatorWaitForOptions { Timeout = DefaultTimeoutMs });
        await facilityButton.ClickAsync();
        await page.WaitForTimeoutAsync(1_000);

        var locationItem = page.Locator($".list-group-item:has-text('{locationName}')").First;
        await locationItem.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
        await locationItem.ClickAsync();
        Console.WriteLine($"Selected facility: {locationName}");

        // Wait for activities to load
        await page.Locator(".schedule-list-row").First
            .WaitForAsync(new LocatorWaitForOptions { Timeout = DefaultTimeoutMs });
        Console.WriteLine("Schedule loaded with activities.");
    }

    public static async Task BookClassAsync(IPage page, string time, DateTime targetDate, string? className = null)
    {
        // The schedule is a scrollable list showing multiple days.
        // Times are displayed without leading zeros (e.g. "7:30" not "07:30").
        var displayTime = time.TrimStart('0');

        // Build the expected day header text (e.g. "19 feb.")
        // Norwegian day header format: "Onsdag 19 feb."
        var dayNumber = targetDate.Day;
        var monthAbbr = targetDate.Month switch
        {
            1 => "jan", 2 => "feb", 3 => "mar", 4 => "apr",
            5 => "mai", 6 => "jun", 7 => "jul", 8 => "aug",
            9 => "sep", 10 => "okt", 11 => "nov", 12 => "des",
            _ => ""
        };
        var dayText = $"{dayNumber} {monthAbbr}.";
        Console.WriteLine($"Looking for day header containing: {dayText}");

        // The schedule lazy-loads days as you scroll. Scroll down until the target day appears.
        var dayHeader = page.Locator($"h4.schedule-list-day-text:has-text('{dayText}')");
        var maxScrollAttempts = 20;
        for (int attempt = 0; attempt < maxScrollAttempts; attempt++)
        {
            if (await dayHeader.CountAsync() > 0 && await dayHeader.IsVisibleAsync())
                break;

            Console.WriteLine($"Day header not found yet, scrolling down (attempt {attempt + 1})...");
            await page.Mouse.WheelAsync(0, 3000);
            await page.WaitForTimeoutAsync(1_500);
        }

        await dayHeader.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        Console.WriteLine($"Found day header: {await dayHeader.InnerTextAsync()}");

        // Scroll the day header into view
        await dayHeader.ScrollIntoViewIfNeededAsync();
        await page.WaitForTimeoutAsync(1_000);

        // Scroll the day header into view so its rows are visible
        await dayHeader.ScrollIntoViewIfNeededAsync();
        await page.WaitForTimeoutAsync(2_000);

        // Find the activity row for our target time within the correct day.
        // Strategy: use JavaScript to find schedule-list-row elements that appear
        // after the target day header and before the next day header.
        var targetRowJs = await page.EvaluateAsync<int?>(@"(args) => {
            const { dayText, displayTime, className } = args;
            const headers = document.querySelectorAll('h4.schedule-list-day-text');
            let targetHeader = null;
            for (const h of headers) {
                if (h.textContent.includes(dayText)) { targetHeader = h; break; }
            }
            if (!targetHeader) return null;

            // Walk siblings/parent to find rows between this header and the next
            // The structure varies, so walk the DOM from the header forward
            let container = targetHeader.closest('.mb-3, .schedule-list-day, div');
            if (!container) return null;

            const allRows = document.querySelectorAll('.schedule-list-row');
            const allRowsArr = Array.from(allRows);

            // Find the position of our header in the document
            const headerRect = targetHeader.getBoundingClientRect();
            let nextHeaderRect = null;
            for (const h of headers) {
                const r = h.getBoundingClientRect();
                if (r.top > headerRect.top + 10) { nextHeaderRect = r; break; }
            }

            for (let i = 0; i < allRowsArr.length; i++) {
                const row = allRowsArr[i];
                const rowRect = row.getBoundingClientRect();
                // Row must be after our header
                if (rowRect.top < headerRect.top) continue;
                // Row must be before next header (if exists)
                if (nextHeaderRect && rowRect.top > nextHeaderRect.top) break;

                const timeEl = row.querySelector('.schedule-list-row-header-item.time');
                if (!timeEl) continue;
                const timeText = timeEl.textContent.trim();
                const startTime = timeText.split('-')[0].trim();
                if (startTime !== displayTime) continue;

                // Filter by class name if specified
                if (className) {
                    const nameEl = row.querySelector('.schedule-list-row-header-item.name, .schedule-list-row-header-item.activity-name');
                    const rowName = nameEl ? nameEl.textContent.trim() : row.textContent;
                    if (!rowName.toLowerCase().includes(className.toLowerCase())) continue;
                }

                const bookeBtn = row.querySelector('button');
                if (bookeBtn && (bookeBtn.textContent.trim() === 'Booke' || bookeBtn.textContent.trim() === 'Venteliste')) return i;
            }
            return null;
        }", new { dayText, displayTime, className });

        if (targetRowJs == null)
            throw new Exception($"Could not find a bookable class at {displayTime} on {targetDate:yyyy-MM-dd}");

        var targetRow = page.Locator(".schedule-list-row").Nth(targetRowJs.Value);
        var rowInfo = await targetRow.InnerTextAsync();
        Console.WriteLine($"Found target row: {rowInfo.Replace('\n', ' ').Trim()}");

        if (targetRow == null)
            throw new Exception($"Could not find a bookable class at {displayTime} on {targetDate:yyyy-MM-dd}");

        // Check if the button says "Venteliste" (waiting list) or "Booke"
        var waitlistButton = targetRow.Locator("button:has-text('Venteliste')");
        var isWaitlist = await IsVisibleWithinAsync(waitlistButton, 2_000);

        if (isWaitlist)
        {
            Console.WriteLine("Class is full. Joining waiting list...");
            await waitlistButton.ScrollIntoViewIfNeededAsync();
            Console.WriteLine("Clicking Venteliste button...");
            await waitlistButton.ClickAsync();
        }
        else
        {
            var bookButton = targetRow.Locator("button:has-text('Booke')");
            await bookButton.ScrollIntoViewIfNeededAsync();
            Console.WriteLine("Clicking Booke button...");
            await bookButton.ClickAsync();
        }

        // A floating confirmation dialog appears with another "Booke" button
        await page.WaitForTimeoutAsync(2_000);
        Console.WriteLine("Waiting for confirmation dialog...");

        // The confirmation dialog is a modal/floating window with a second "Booke" button
        var confirmButton = page.Locator(".modal button:has-text('Booke'), .dialog button:has-text('Booke'), [role='dialog'] button:has-text('Booke'), .offcanvas button:has-text('Booke'), .popup button:has-text('Booke')").First;
        if (!await IsVisibleWithinAsync(confirmButton, 5_000))
        {
            // Fallback: look for any new "Booke" button that appeared (not inside a schedule row)
            confirmButton = page.Locator("button:has-text('Booke')").Last;
        }
        await confirmButton.WaitForAsync(new LocatorWaitForOptions { Timeout = DefaultTimeoutMs });
        Console.WriteLine("Clicking confirmation Booke button...");
        await confirmButton.ClickAsync();

        // Wait for booking confirmation (success message or state change to "Avbook")
        await page.WaitForTimeoutAsync(3_000);
        Console.WriteLine("Booking confirmed!");
    }

    private static async Task<bool> IsVisibleWithinAsync(ILocator locator, int timeoutMs)
    {
        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions { Timeout = timeoutMs });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
