import sqlite3, os

MAIN = r"C:\Users\10638\AppData\Local\EasyMovie\EasyMovie.db"
print("db size MB =", round(os.path.getsize(MAIN) / 1024 / 1024, 2))

c = sqlite3.connect(MAIN)
q = lambda s: c.execute(s).fetchone()[0]

movies = q("SELECT COUNT(*) FROM Movies")
links = q("SELECT COUNT(*) FROM MovieTags")
print(f"Movies={movies}  MovieTags链接={links}")

# JOIN 后的行数放大
joined = q("SELECT COUNT(*) FROM Movies m LEFT JOIN MovieTags mt ON mt.MovieId = m.Id")
print(f"LEFT JOIN MovieTags 后行数 = {joined}  放大倍数 = {joined/max(movies,1):.2f}x")

# 进一步 Include(Tag) 再 JOIN 一次，行数不变但列变多
print("\n-- PosterData 体积占比 --")
q2 = c.execute("SELECT Id, LENGTH(PosterData) FROM Movies WHERE PosterData IS NOT NULL").fetchall()
poster_rows = len(q2)
poster_bytes = sum(r[1] for r in q2)
all_bytes = 0
for (mid,) in c.execute("SELECT Id FROM Movies"):
    pass
print(f"有海报的电影 = {poster_rows}/{movies}  海报总字节 = {poster_bytes/1024/1024:.2f} MB")
print(f"平均单张海报 = {poster_bytes/max(poster_rows,1)/1024:.1f} KB")

print("\n-- 模拟 SearchAsync(SingleQuery JOIN) 实际从磁盘读出的 Movie 列数据 --")
# 估算：JOIN 后每行都带 PosterData
est = joined * (poster_bytes / max(poster_rows, 1))
print(f"若 JOIN 结果每行都携带 PosterData: {est/1024/1024:.1f} MB  (vs 不 JOIN 时 {poster_bytes/1024/1024:.2f} MB)")

# 实际 SQL: 只看一页 20 部的影响
sample = c.execute(
    "SELECT COUNT(*) FROM Movies m LEFT JOIN MovieTags mt ON mt.MovieId=m.Id "
    "WHERE m.Id IN (SELECT Id FROM Movies ORDER BY CreatedAt DESC LIMIT 20)"
).fetchone()[0]
print(f"\n取最新 20 部时 JOIN 行数 = {sample} (翻页每页都要付这个代价)")

# 每部片平均几个标签
print("每部平均标签数 =", round(links / max(movies, 1), 2))
print("单部最多标签数 =", c.execute(
    "SELECT COUNT(*) FROM MovieTags GROUP BY MovieId ORDER BY COUNT(*) DESC LIMIT 1").fetchone()[0])

print("\n-- 对比：不 Include 时的窄投影只需读这些列 --")
narrow = c.execute(
    "SELECT Id,Title,Year,Rating,WatchStatus,IsFavorite,CategoryId FROM Movies ORDER BY CreatedAt DESC LIMIT 20"
).fetchall()
print(f"窄投影 20 行, 列数=7, 无 BLOB → 数据量可以忽略")

c.close()
