"""Create a realistic messy "Downloads" folder of SAMPLE files (all businesses and people are fictional).

Usage: python samples/make_messy_folder.py <output-folder>
Writes the files plus samples/expected.json (what a good tidy-up looks like). Needs Pillow for the images.
"""
import json, os, sys, time, zipfile, zlib
from PIL import Image, ImageDraw, ImageFont

out = sys.argv[1] if len(sys.argv) > 1 else "messy-downloads"
os.makedirs(out, exist_ok=True)
HERE = os.path.dirname(os.path.abspath(__file__))
NOTE = "SAMPLE DOCUMENT - FICTIONAL - FOR TESTING"

def pdf(name, lines):
    text = " ".join(lines) + "  " + NOTE
    content = ("BT /F1 11 Tf 72 740 Td " + " ".join(f"({l.replace('(', '[').replace(')', ']')}) Tj 0 -16 Td" for l in lines + [NOTE]) + " ET").encode("latin-1")
    stream = zlib.compress(content)
    parts = [b"%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n",
             b"3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >> endobj\n",
             b"5 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj\n",
             f"4 0 obj << /Length {len(stream)} /Filter /FlateDecode >>\nstream\n".encode(), stream, b"\nendstream\nendobj\ntrailer << /Root 1 0 R >>\n%%EOF\n"]
    open(os.path.join(out, name), "wb").write(b"".join(parts))

def docx(name, paras):
    with zipfile.ZipFile(os.path.join(out, name), "w") as z:
        z.writestr("[Content_Types].xml", '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        body = "".join(f"<w:p><w:r><w:t>{p}</w:t></w:r></w:p>" for p in paras + [NOTE])
        z.writestr("word/document.xml", f'<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}</w:body></w:document>')

def xlsx(name, strings):
    with zipfile.ZipFile(os.path.join(out, name), "w") as z:
        z.writestr("[Content_Types].xml", '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>')
        si = "".join(f"<si><t>{s}</t></si>" for s in strings + [NOTE])
        z.writestr("xl/sharedStrings.xml", f'<?xml version="1.0"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">{si}</sst>')

def text(name, body):
    open(os.path.join(out, name), "w", encoding="utf-8").write(body + "\n\n" + NOTE + "\n")

def font(size):
    for p in ["/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "C:/Windows/Fonts/segoeui.ttf"]:
        if os.path.exists(p): return ImageFont.truetype(p, size)
    return ImageFont.load_default()

FILES = []
def want(name, folder, keywords=()):
    FILES.append({"file": name, "folder": folder, "keywords": list(keywords)})

pdf("document(7).pdf", ["Chinook Mobile", "Monthly bill", "Bill date: October 14, 2026", "Account 0000-1111", "Plan: Unlimited Talk & Text", "Amount due: $85.12 by November 4, 2026"])
want("document(7).pdf", "Bills & Receipts", ["chinook", "bill"])
pdf("statement_0923.pdf", ["Prairie Credit Union", "Chequing account statement", "Statement period: September 1 - 30, 2026", "Opening balance $2,140.55", "Closing balance $1,876.02"])
want("statement_0923.pdf", "Banking & Taxes", ["statement"])
pdf("invoice_88213.pdf", ["Foothills Web Studio", "INVOICE #88213", "Invoice date: 2026-09-28", "Website maintenance - September", "Subtotal $400.00  GST 5% $20.00  Total $420.00"])
want("invoice_88213.pdf", "Bills & Receipts", ["invoice"])
pdf("download.pdf", ["Bow River Medical Laboratory", "Patient report", "Collected: 2026-10-02", "Test: Complete blood count", "Result: within normal range"])
want("download.pdf", "Medical", ["lab"])
docx("Untitled document.docx", ["Residential Tenancy Agreement", "Landlord: Sample Property Co.  Tenant: A. Example", "Premises: 123 Example Ave NW, Calgary, Alberta", "Term: November 1, 2026 to October 31, 2027", "Monthly rent: $1,650"])
want("Untitled document.docx", "Home & Car", ["lease", "tenancy", "rental"])
docx("resume_final_FINAL.docx", ["Alex Example", "Resume", "Software developer, Calgary, AB", "Experience: C#, .NET, SQL", "Education: BSc Computer Science"])
want("resume_final_FINAL.docx", "IDs & Personal", ["resume"])
xlsx("Q3_report_v2.xlsx", ["Q3 2026 sales report", "Region", "Calgary", "Edmonton", "Red Deer", "Revenue", "Target", "Prepared for: Operations team"])
want("Q3_report_v2.xlsx", "Work", ["q3", "report", "sales"])
text("booking-confirmation.html", "<html><body><h1>Aurora Air - Booking confirmed</h1><p>Flight AA 214 Calgary (YYC) to Vancouver (YVR)</p><p>Departs 2026-11-20 07:15</p><p>Passenger: A. Example</p><p>Confirmation code: SAMPLE1</p></body></html>")
want("booking-confirmation.html", "Travel", ["flight", "booking", "aurora"])
text("notes.txt", "COMP 2510 - Assignment 3\nDue: November 2, 2026\nTopic: data structures (linked lists)\nRemember to submit on the course portal.")
want("notes.txt", "School", ["assignment"])

# Images: a receipt photo (from Shoebox's samples if present), a screenshot, a landscape photo
receipt_src = os.path.join(HERE, "..", "..", "Shoebox", "samples", "receipts", "02_coffee.jpg")
if os.path.exists(receipt_src):
    Image.open(receipt_src).save(os.path.join(out, "scan0001.jpg"), quality=88)
else:
    im = Image.new("RGB", (560, 760), (245, 245, 240)); d = ImageDraw.Draw(im)
    for i, l in enumerate(["Chinook Coffee House", "Latte 5.75", "Muffin 2.75", "GST 0.43", "TOTAL $8.93", "SAMPLE RECEIPT - FOR TESTING"]):
        d.text((40, 60 + i * 50), l, fill=(20, 20, 20), font=font(30))
    im.save(os.path.join(out, "scan0001.jpg"), quality=88)
want("scan0001.jpg", "Bills & Receipts", ["receipt", "coffee", "chinook"])  # with --read-images; Photos without

im = Image.new("RGB", (1280, 720), (250, 250, 252)); d = ImageDraw.Draw(im)
d.rectangle([0, 0, 1280, 56], fill=(40, 44, 52)); d.text((20, 14), "Settings - Display", fill=(255, 255, 255), font=font(24))
d.text((60, 120), "Screen resolution: 1920 x 1080", fill=(30, 30, 30), font=font(28))
im.save(os.path.join(out, "Screenshot 2026-10-12 101522.png"))
want("Screenshot 2026-10-12 101522.png", "Screenshots")

im = Image.new("RGB", (1200, 800), (135, 190, 235)); d = ImageDraw.Draw(im)
d.polygon([(0, 560), (300, 260), (520, 470), (760, 200), (1200, 560), (1200, 800), (0, 800)], fill=(90, 110, 125))
d.polygon([(260, 300), (300, 260), (340, 300)], fill=(250, 250, 250)); d.polygon([(720, 240), (760, 200), (800, 240)], fill=(250, 250, 250))
d.rectangle([0, 620, 1200, 800], fill=(70, 120, 60))
im.save(os.path.join(out, "IMG_2045.jpg"), quality=90)
want("IMG_2045.jpg", "Photos")

open(os.path.join(out, "SetupTool_x64.exe"), "wb").write(b"MZ" + b"\0" * 2046)   # not a real program
want("SetupTool_x64.exe", "Installers")
with zipfile.ZipFile(os.path.join(out, "project-files.zip"), "w") as z: z.writestr("readme.txt", NOTE)
want("project-files.zip", "Archives")
open(os.path.join(out, "voice memo.m4a"), "wb").write(b"\0" * 4096)
want("voice memo.m4a", "Music & Video")
open(os.path.join(out, "big-video.mp4.crdownload"), "wb").write(b"\0" * 1024)   # still downloading: must be skipped

# Make everything look like it was downloaded over the past few weeks (and not "just written")
now = time.time()
for i, name in enumerate(sorted(os.listdir(out))):
    t = now - (2 + i) * 86400
    os.utime(os.path.join(out, name), (t, t))

json.dump(FILES, open(os.path.join(HERE, "expected.json"), "w"), indent=2)
print(f"wrote {len(os.listdir(out))} files to {out}")
