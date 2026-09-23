import sqlite3, sys

CACHE = r"C:\Users\10638\AppData\Local\EasyMovie\cache.db"
MAIN = r"C:\Users\10638\AppData\Local\EasyMovie\EasyMovie.db"

titles = sys.argv[1:]

c = sqlite3.connect(CACHE)
c.row_factory = sqlite3.Row
cols = [r[1] for r in c.execute("PRAGMA table_info(CachedMovies)")]
print("cache.db CachedMovies columns:", cols)

have = set(cols)
def pick(*names):
    for n in names:
        if n in have:
            return n
    return None

c_title = pick("NormTitle", "Title")
c_orig = pick("NormOriginal", "OriginalTitle")
c_year = pick("Year")
c_rate = pick("Rating")
c_src = pick("Source")
c_ext = pick("ExternalId")

for t in titles:
    print("================", t)
    for row in c.execute(f"SELECT * FROM CachedMovies WHERE {c_title} = ?", (t,)):
        d = dict(row)
        print("   ", {k: d.get(k) for k in (c_title, c_orig, c_year, c_rate, c_src, c_ext)})
c.close()

m = sqlite3.connect(MAIN)
print("\n-- 主库这些片当前 ExternalRating --")
for t in titles:
    for r in m.execute("SELECT Id, Title, Year, ExternalRating, RatingSource FROM Movies WHERE Title = ?", (t,)):
        print("   ", r)
m.close()
