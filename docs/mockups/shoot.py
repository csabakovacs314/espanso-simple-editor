from playwright.sync_api import sync_playwright
import pathlib
html = pathlib.Path("mockup.html").resolve().as_uri()
with sync_playwright() as p:
    b = p.chromium.launch()
    pg = b.new_page(viewport={"width": 1300, "height": 3400}, device_scale_factor=1.5)
    pg.goto(html)
    pg.wait_for_timeout(300)
    for i in ["main", "triggers", "html", "load"]:
        pg.locator(f"#{i}").screenshot(path=f"espanso-editor-{i}.png")
    b.close()
