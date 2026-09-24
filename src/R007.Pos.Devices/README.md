# R007.Pos.Devices

Vendor-neutral hardware abstractions for the POS:

| Interface | Device |
|---|---|
| `IReceiptPrinter` | 80 mm thermal printer, ESC/POS |
| `INfcReader` | NFC staff badges / membership cards |
| `IBarcodeScanner` | Barcode / QR scanner |
| `ICashDrawer` | Cash drawer (usually kicked via printer) |
| `ICustomerDisplay` | Optional customer-facing display |

`Simulated*` implementations are provided for development and tests. Real drivers will live in
separate, vendor-specific projects/adapters selected by configuration — nothing in this library
may reference a vendor SDK.

## Real implementations in this library

| Class | Purpose |
|---|---|
| `EscPosRenderer` | `ReceiptDocument` -> ESC/POS bytes (80 mm, 48 cols, QR, cut, drawer kick) and plain-text preview |
| `EscPosReceiptPrinter` + `IRawPrinterPort` | Real printer = renderer + a byte port |
| `WindowsSpoolerPort` | Raw ESC/POS to a Windows printer queue via winspool (Windows only) |
| `FileReceiptPrinter` / `ConsoleReceiptPrinter` | `.txt` preview + `.bin` bytes / console preview (tests, demos, macOS) |
| `PrinterCashDrawer` | Drawer kicked through the printer port |
| `KeyboardWedgeDecoder` | Detects scanner/NFC "keyboard wedge" bursts in the character stream |
