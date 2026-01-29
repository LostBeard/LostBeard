import os
import re
import requests
import textwrap

# --- CONFIGURATION ---
USERNAME = "LostBeard"
MAX_REPOS = 20
README_PATH = "README.md"
OUTPUT_DIR = "pins"

# --- SVG TEMPLATE ---
# Simple dark mode styled SVG inspired by GitHub's native pins
SVG_TEMPLATE = """
<svg width="400" height="120" viewBox="0 0 400 120" fill="none" xmlns="http://www.w3.org/2000/svg">
    <style>
    .bg {{ fill: #0d1117; stroke: #30363d; stroke-width: 1; }}
    .title {{ font: 600 14px 'Segoe UI', Ubuntu, Sans-Serif; fill: #58a6ff; }}
    .desc {{ font: 400 12px 'Segoe UI', Ubuntu, Sans-Serif; fill: #8b949e; }}
    .stat {{ font: 400 12px 'Segoe UI', Ubuntu, Sans-Serif; fill: #8b949e; }}
    .icon {{ fill: #8b949e; }}
    </style>
    <rect x="0.5" y="0.5" width="399" height="119" rx="6" class="bg"/>
    <text x="16" y="28" class="title">{name}</text>
    <text x="16" y="55" class="desc">
        {desc_lines}
    </text>
    <g transform="translate(16, 95)">
        <circle cx="6" cy="6" r="6" fill="{lang_color}"/>
        <text x="20" y="10" class="stat">{language}</text>
        
        <g transform="translate({star_offset}, 0)">
            <path class="icon" d="M8 .25a.75.75 0 01.673.418l1.882 3.815 4.21.612a.75.75 0 01.416 1.279l-3.046 2.97.719 4.192a.75.75 0 01-1.088.791L8 12.347l-3.766 1.98a.75.75 0 01-1.088-.79l.72-4.194L.818 6.374a.75.75 0 01.416-1.28l4.21-.611L7.327.668A.75.75 0 018 .25z" transform="scale(0.8) translate(0,2)"/>
            <text x="18" y="10" class="stat">{stars}</text>
        </g>

        <g transform="translate({fork_offset}, 0)">
            <path class="icon" d="M5 3.25a.75.75 0 11-1.5 0 .75.75 0 011.5 0zm0 2.122a2.25 2.25 0 10-1.5 0v.878A2.25 2.25 0 005.75 8.5h1.5v2.128a2.251 2.251 0 101.5 0V8.5h1.5a2.25 2.25 0 002.25-2.25v-.878a2.25 2.25 0 10-1.5 0v.878a.75.75 0 01-.75.75h-4.5A.75.75 0 015 6.25v-.878zm3.75 7.378a.75.75 0 11-1.5 0 .75.75 0 011.5 0zm3-8.75a.75.75 0 100-1.5.75.75 0 000 1.5z" transform="scale(0.8) translate(0,2)"/>
            <text x="18" y="10" class="stat">{forks}</text>
        </g>
    </g>
</svg>
"""

def get_data(token):
    headers = {"Authorization": f"token {token}"}
    # Get repos sorted by stars
    url = f"https://api.github.com/users/{USERNAME}/repos?sort=pushed&per_page=100&type=owner"
    repos = requests.get(url, headers=headers).json()
    
    # Filter forks and sort by stars
    repos = [r for r in repos if not r.get('fork', False)]
    repos.sort(key=lambda r: r['stargazers_count'], reverse=True)
    
    # Optional: Fetch language colors (fallback to grey)
    try:
        colors = requests.get("https://raw.githubusercontent.com/ozh/github-colors/master/colors.json").json()
    except:
        colors = {}
        
    return repos[:MAX_REPOS], colors

def generate_svg(repo, colors):
    # Prepare Data
    desc = repo['description'] or "No description provided."
    # Wrap text approx 55 chars
    lines = textwrap.wrap(desc, width=55)[:2] 
    desc_svg = ""
    for i, line in enumerate(lines):
        desc_svg += f'<tspan x="16" dy="{20 if i > 0 else 0}">{line}</tspan>'
    
    lang = repo['language'] or "Text"
    lang_color = colors.get(lang, {}).get("color", "#ccc")
    
    # Calculate spacing for stats
    # Approx width calc: 20px icon + 7px per char
    lang_width = 30 + (len(lang) * 7)
    star_width = 30 + (len(str(repo['stargazers_count'])) * 7)
    
    return SVG_TEMPLATE.format(
        name=repo['name'],
        desc_lines=desc_svg,
        language=lang,
        lang_color=lang_color,
        stars=repo['stargazers_count'],
        forks=repo['forks_count'],
        star_offset=lang_width,
        fork_offset=lang_width + star_width
    )

def main():
    token = os.environ.get("GITHUB_TOKEN")
    repos, colors = get_data(token)
    
    if not os.path.exists(OUTPUT_DIR):
        os.makedirs(OUTPUT_DIR)
        
    md_content = ""
    
    for repo in repos:
        # Generate and save SVG
        svg_content = generate_svg(repo, colors)
        filename = f"{OUTPUT_DIR}/{repo['name']}.svg"
        with open(filename, "w", encoding="utf-8") as f:
            f.write(svg_content)
            
        # Add to Markdown List
        md_content += f'<a href="{repo["html_url"]}"><img src="./{filename}" width="48%" alt="{repo["name"]}" /></a> \n'

    # Read README
    with open(README_PATH, 'r', encoding='utf-8') as f:
        readme = f.read()
    
    # Replace content between markers
    pattern = r'()(.*?)()'
    replacement = f'\\1\n{md_content}\n\\3'
    new_readme = re.sub(pattern, replacement, readme, flags=re.DOTALL)
    
    with open(README_PATH, 'w', encoding='utf-8') as f:
        f.write(new_readme)

if __name__ == "__main__":
    main()
