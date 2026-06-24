using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;

namespace aclearningutil.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LearningContentCategoriesController : ControllerBase
    {
        private readonly AppDbContext _dbContext;
        private readonly ILogger<LearningContentCategoriesController> _logger;

        public LearningContentCategoriesController(AppDbContext dbContext, ILogger<LearningContentCategoriesController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        // GET: api/LearningContentCategories
        [HttpGet]
        public async Task<ActionResult<List<LearningContentCategory>>> GetAll()
        {
            var categories = await _dbContext.LearningContentCategories
                .OrderBy(c => c.Id)
                .ToListAsync();
            return categories;
        }

        // GET: api/LearningContentCategories/5
        [HttpGet("{id}")]
        public async Task<ActionResult<LearningContentCategory>> GetById(int id)
        {
            var category = await _dbContext.LearningContentCategories.FindAsync(id);
            if (category == null)
            {
                return NotFound();
            }
            return category;
        }
    }
}
