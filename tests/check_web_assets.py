"""Static UI integrity: inline JS syntax and internal HTML navigation assets."""
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urlsplit
import subprocess,tempfile
root=Path("wwwroot")
class Page(HTMLParser):
    def __init__(self):
        super().__init__();self.inline=False;self.scripts=[];self.links=[];self.ids=set();self.duplicates=[]
    def handle_starttag(self,tag,attrs):
        a=dict(attrs)
        if "id" in a:
            if a["id"] in self.ids:self.duplicates.append(a["id"])
            self.ids.add(a["id"])
        if tag=="script" and "src" not in a and a.get("type","") in ("","text/javascript","module"):
            self.inline=True;self.scripts.append("")
        if tag in ("a","link","script"):
            value=a.get("href") or a.get("src") or ""
            path=urlsplit(value).path
            if path.startswith("/") and path.endswith((".html",".js",".css")):self.links.append(path)
    def handle_data(self,data):
        if self.inline:self.scripts[-1]+=data
    def handle_endtag(self,tag):
        if tag=="script":self.inline=False
for file in sorted(root.glob("*.html")):
    p=Page();p.feed(file.read_text(encoding="utf-8"))
    assert not p.duplicates,(file,p.duplicates)
    for link in p.links:assert (root/link.lstrip("/")).is_file(),(file,link)
    for i,script in enumerate(p.scripts):
        with tempfile.NamedTemporaryFile(suffix=".js",mode="w",encoding="utf-8") as tmp:
            tmp.write(script);tmp.flush()
            result=subprocess.run(["node","--check",tmp.name],capture_output=True,text=True)
            assert result.returncode==0,(file,i,result.stderr)
for file in root.glob("*.js"):subprocess.run(["node","--check",str(file)],check=True)
subprocess.run(["node", "tests/embedded_signup_ui.cjs"], check=True)
print("PASS: page script syntax, unique IDs and static asset links")
