# Otueke.Pos.Devices

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
