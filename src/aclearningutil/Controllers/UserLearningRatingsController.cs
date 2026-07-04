using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;

namespace aclearningutil.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class UserLearningRatingsController : ControllerBase
    {
        private readonly AppDbContext _dbContext;
        private readonly ILogger<UserLearningRatingsController> _logger;

        public UserLearningRatingsController(AppDbContext dbContext, ILogger<UserLearningRatingsController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        private string GetUserId()
        {
            return User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub")
                ?? string.Empty;
        }

        // GET: api/UserLearningRatings?contentId=1&itemId=5&page=1&pageSize=50
        [HttpGet]
        public async Task<ActionResult<List<UserLearningRating>>> GetAll([FromQuery] int? contentId = null, [FromQuery] int? itemId = null, [FromQuery] int? page = null, [FromQuery] int? pageSize = null, CancellationToken cancellationToken = default)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            IQueryable<UserLearningRating> query = _dbContext.UserLearningRatings
                .Include(r => r.Content)
                .Where(r => r.UserId == userId);

            if (contentId.HasValue)
            {
                query = query.Where(r => r.ContentId == contentId.Value);
            }

            if (itemId.HasValue)
            {
                query = query.Where(r => r.ItemId == itemId.Value);
            }

            // Apply pagination
            const int maxPageSize = 200;
            var skip = ((page ?? 1) - 1) * Math.Clamp(pageSize ?? 50, 1, maxPageSize);
            var take = Math.Clamp(pageSize ?? 50, 1, maxPageSize);

            var ratings = await query.OrderByDescending(r => r.ScoreDate).Skip(skip).Take(take).ToListAsync(cancellationToken);
            return ratings;
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<UserLearningRating>> GetById(int id, CancellationToken cancellationToken)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            var rating = await _dbContext.UserLearningRatings
                .Include(r => r.Content)
                .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, cancellationToken);

            if (rating == null)
            {
                return NotFound();
            }
            return rating;
        }

        [HttpPost]
        public async Task<ActionResult<UserLearningRating>> Create([FromBody] UserLearningRating rating, CancellationToken cancellationToken)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            if (rating.Rating < 1 || rating.Rating > 5)
            {
                return BadRequest("Rating must be between 1 and 5.");
            }

            var contentExists = await _dbContext.LearningContents.AnyAsync(c => c.Id == rating.ContentId, cancellationToken);
            if (!contentExists)
            {
                return BadRequest("ContentId does not exist.");
            }

            rating.UserId = userId;
            if (rating.ScoreDate == default)
            {
                rating.ScoreDate = DateTime.Today;
            }

            _dbContext.UserLearningRatings.Add(rating);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = rating.Id }, rating);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] UserLearningRating rating, CancellationToken cancellationToken)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            if (rating.Rating < 1 || rating.Rating > 5)
            {
                return BadRequest("Rating must be between 1 and 5.");
            }

            var existing = await _dbContext.UserLearningRatings
                .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, cancellationToken);
            if (existing == null)
            {
                return NotFound();
            }

            var contentExists = await _dbContext.LearningContents.AnyAsync(c => c.Id == rating.ContentId, cancellationToken);
            if (!contentExists)
            {
                return BadRequest("ContentId does not exist.");
            }

            existing.ContentId = rating.ContentId;
            existing.ItemId = rating.ItemId;
            existing.ScoreDate = rating.ScoreDate;
            existing.Rating = rating.Rating;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized("User ID not found in token.");
            }

            var rating = await _dbContext.UserLearningRatings
                .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, cancellationToken);
            if (rating == null)
            {
                return NotFound();
            }

            _dbContext.UserLearningRatings.Remove(rating);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return NoContent();
        }
    }
}
