import sqlite3, shutil, datetime, os

CACHE = r"C:\Users\10638\AppData\Local\EasyMovie\cache.db"

c = sqlite3.connect(CACHE)
total = c.execute("SELECT COUNT(*) FROM CachedMovies").fetchone()[0]
bad = c.execute("SELECT COUNT(*) FROM CachedMovies WHERE Rating IS NOT NULL AND Rating <= 0").fetchone()[0]
bad_douban = c.execute("SELECT COUNT(*) FROM CachedMovies WHERE Rating IS NOT NULL AND Rating <= 0 AND Source = 'douban'").fetchone()[0]
print(f"total={total} rating<=0={bad} rating<=0_douban={bad_douban}")

rows = c.execute(
    "SELECT Id, Title, Year, Rating, Source FROM CachedMovies "
    "WHERE Rating IS NOT NULL AND Rating <= 0 AND Source = 'douban' ORDER BY Id DESC"
).fetchall()
for r in rows:
    print("  ", r)

if rows:
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    bak = os.path.join(os.path.dirname(CACHE), "backups", f"cache_before_zero_clean_{stamp}.db")
    os.makedirs(os.path.dirname(bak), exist_ok=True)
    c.close()
    shutil.copy2(CACHE, bak)
    print("backup ->", bak)
    c = sqlite3.connect(CACHE)
    ids = [r[0] for r in rows]
    c.execute(
        "DELETE FROM CachedMovies WHERE Id IN (%s)" % ",".join("?" * len(ids)), ids
    )
    c.commit()
    print("deleted", len(ids), "rows; remaining rating<=0 =",
          c.execute("SELECT COUNT(*) FROM CachedMovies WHERE Rating IS NOT NULL AND Rating <= 0").fetchone()[0])
c.close()
