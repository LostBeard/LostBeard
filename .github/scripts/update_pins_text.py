import os
import re
import requests

# --- CONFIGURATION ---
USERNAME = "LostBeard"
MAX_REPOS = 10  # Increase this to whatever number you want
README_PATH = "README.md"

def get_top_repos(token):
    headers = {"Authorization": f"token {token}"}
    url = f"https://api.github.com/users/{USERNAME}/repos?sort=pushed&per_page=100&type=owner"
    
    try:
        response = requests.get(url, headers=headers)
        response.raise_for_status()
        repos = response.json()
    except Exception as e:
        print(f"Error fetching repos: {e}")
        return []

    # Filter out forks and sort by stars
    repos = [r for r in repos if not r.get('fork', False)]
    repos.sort(key=lambda r: r['stargazers_count'], reverse=True)
    
    return repos[:MAX_REPOS]

def generate_markdown_table(repos):
    # Table Header
    table = "| Project | ⭐ Stars | 🍴 Forks | Language |\n"
    table += "| :--- | :---: | :---: | :--- |\n"
    
    for r in repos:
        name = r['name']
        url = r['html_url']
        desc = r['description'] if r['description'] else " "
        stars = r['stargazers_count']
        forks = r['forks_count']
        # Handle cases where language is None
        lang = r['language'] if r['language'] else "Text"
        
        # Clean description to prevent breaking the table (remove pipes and newlines)
        desc = desc.replace("|", "-").replace("\n", " ")
        
        # Limit description length to keep table tidy (optional)
        if len(desc) > 100:
            desc = desc[:97] + "..."

        # Format: **[Name](Link)** <br> Description | Stars | Forks | Lang
        row = f"| **[{name}]({url})**<br>{desc} | {stars} | {forks} | {lang} |\n"
        table += row
        
    return table

def update_readme(new_content):
    if not os.path.exists(README_PATH):
        print("README.md not found!")
        return

    with open(README_PATH, 'r', encoding='utf-8') as f:
        readme = f.read()

    # Regex to look for the markers
    pattern = r'()(.*?)()'
    replacement = f'\\1\n{new_content}\n\\3'
    
    # Check if markers exist
    if not re.search(pattern, readme, flags=re.DOTALL):
        print("Markers not found in README.md. Please add and ")
        return

    new_readme = re.sub(pattern, replacement, readme, flags=re.DOTALL)
    
    with open(README_PATH, 'w', encoding='utf-8') as f:
        f.write(new_readme)

if __name__ == "__main__":
    token = os.environ.get("GITHUB_TOKEN")
    top_repos = get_top_repos(token)
    if top_repos:
        markdown_table = generate_markdown_table(top_repos)
        update_readme(markdown_table)
