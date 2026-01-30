import os
import requests

# --- CONFIGURATION ---
USERNAME = "LostBeard"
MAX_REPOS = 10
README_PATH = "README.md"
START_MARKER = "<!-- PINS_START -->"
END_MARKER = "<!-- PINS_END -->"

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
    table = "| Project | ⭐ Stars | 🍴 Forks | Language |\n"
    table += "| :--- | :---: | :---: | :--- |\n"
    
    for r in repos:
        name = r['name']
        url = r['html_url']
        desc = r['description'] or " "
        stars = r['stargazers_count']
        forks = r['forks_count']
        lang = r['language'] or "Text"
        
        # Sanitize description
        desc = desc.replace("|", "-").replace("\n", " ")
        if len(desc) > 80: desc = desc[:77] + "..."

        row = f"| **[{name}]({url})**<br>{desc} | {stars} | {forks} | {lang} |\n"
        table += row
        
    return table

def update_readme(new_table):
    if not os.path.exists(README_PATH):
        print("README.md not found!")
        return

    with open(README_PATH, 'r', encoding='utf-8') as f:
        content = f.read()

    # Find the positions of the markers
    start_pos = content.find(START_MARKER)
    end_pos = content.find(END_MARKER)

    if start_pos == -1 or end_pos == -1:
        print(f"Error: Markers '{START_MARKER}' and '{END_MARKER}' not found in README.")
        return

    # Validations to ensure we don't delete the whole file by accident
    if end_pos < start_pos:
        print("Error: End marker found before Start marker.")
        return

    # --- THE FIX: Precise String Splicing ---
    # Keep everything BEFORE the start marker (including the marker itself)
    pre_content = content[:start_pos + len(START_MARKER)]
    
    # Keep everything AFTER the end marker (including the marker itself)
    post_content = content[end_pos:]
    
    # Combine: Pre + New Table + Post
    new_content = f"{pre_content}\n{new_table}\n{post_content}"
    
    # Write only if changed
    if new_content != content:
        with open(README_PATH, 'w', encoding='utf-8') as f:
            f.write(new_content)
        print("README.md updated successfully.")
    else:
        print("No changes needed.")

if __name__ == "__main__":
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        top_repos = get_top_repos(token)
        if top_repos:
            table = generate_markdown_table(top_repos)
            update_readme(table)
    else:
        print("GITHUB_TOKEN not found.")
