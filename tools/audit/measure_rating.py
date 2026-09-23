import sqlite3, sys

MAIN = r"C:\Users\10638\AppData\Local\EasyMovie\EasyMovie.db"

def q(db, sql):
    c = sqlite3.connect(db)
    try:
        return c.execute(sql).fetchall()
    finally:
        c.close()

def snapshot():
    total = q(MAIN, "SELECT COUNT(*) FROM Movies")[0][0]
    has = q(MAIN, "SELECT COUNT(*) FROM Movies WHERE ExternalRating IS NOT NULL")[0][0]
    miss = total - has
    miss20 = q(MAIN, "SELECT COUNT(*) FROM Movies WHERE ExternalRating IS NULL AND (Year IS NULL OR Year >= 2020)")[0][0]
    return total, has, miss, miss20

if __name__ == "__main__":
    t, h, m, m20 = snapshot()
    print(f"TOTAL={t} HAS_RATING={h} MISSING={m} MISSING_2020PLUS={m20} COVERAGE={h*100.0/max(t,1):.1f}%")
    rows = q(MAIN, "SELECT Id, Title, Year FROM Movies WHERE ExternalRating IS NULL ORDER BY COALESCE(Year,0) DESC, Id")
    print(f"--MISSING LIST ({len(rows)})--")
    for r in rows:
        print(f"  [{r[0]}] {r[1]} ({r[2]})")
