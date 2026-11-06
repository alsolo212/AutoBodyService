using Microsoft.EntityFrameworkCore;

namespace AutoBodyService.Infrastructure.Persistence;

public sealed class AutoBodyServiceDbContext(DbContextOptions<AutoBodyServiceDbContext> options)
    : DbContext(options);
