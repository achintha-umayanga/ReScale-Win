# Window State Saver (Global DPI Normalizer)

A universal, ultra-lightweight background tool for Windows 10 and 11 that solves the multi-monitor DPI mismatch for **closed and reopened applications**.

---

### The Problem It Solves

When you use multiple monitors with different DPI scalings (e.g., 100% and 150%, or 4K at 200% and 1080p at 100%):
1. You open an app (e.g. Task Manager, File Explorer, Chrome) on your 100% monitor, adjust its size, and **close it**.
2. The app hardcodes its raw physical pixel size (e.g. `900px × 650px`) into the registry.
3. You **unplug** your laptop and launch the app on your high-DPI (150% or 200%) laptop display.
4. The app requests `900px × 650px`, which renders **miniature and cramped** on high DPI.
5. You stretch it to 1400px, close it, and plug back into the 100% monitor.
6. The app opens at 1400px, taking over the entire 1080p screen (**oversized**).

---

### How This Tool Solves It (Universally for Any Monitor & Scale)

Whenever any application window opens or gains focus:
1. It looks up the monitor DPI the app was last saved on (`SavedDpi`), and the DPI of the monitor it just spawned on (`CurrentDpi`).
2. If the monitor DPI differs, it instantly converts the window to the exact correct physical dimensions using the universal formula:
   $$\text{Target Width} = \text{Saved Width} \times \left(\frac{\text{Current DPI}}{\text{Saved DPI}}\right)$$
   $$\text{Target Height} = \text{Saved Height} \times \left(\frac{\text{Current DPI}}{\text{Saved DPI}}\right)$$
3. **Result:**
   * An app sized to $900 \times 650$ on a 100% display will automatically open at **$1350 \times 975$** on a 150% laptop display ($900 \times 1.5$).
   * An app sized to $1400 \times 950$ on a 150% display will automatically open at **$933 \times 633$** on a 100% monitor ($1400 \div 1.5$).
   * In both directions, the visual layout, button sizes, and proportions remain **identical**.

---

### How to Run

1. Double-click [**`Run-WindowStateSaver.bat`**](file:///C:/Users/AchiYa/Desktop/Window%20State%20Saver/Run-WindowStateSaver.bat) (or run [`publish\WindowStateSaver.exe`](file:///C:/Users/AchiYa/Desktop/Window%20State%20Saver/publish/WindowStateSaver.exe)).
2. It sits silently in your **Windows System Tray** (near the clock) using 0% CPU.
3. Right-click the tray icon and check **"Start with Windows"** so it automatically protects your window sizes every time you boot.

### Controls

* **`Ctrl + Alt + S`**: Instantly normalize the active window.
* **`Ctrl + Alt + A`**: Instantly normalize all open windows across all displays.
* **Open Saved Rules (JSON)**: Right-click the tray icon to view or edit the learned sizes.
