import os
import requests

# --- CONFIGURATION ---
USERNAME = "LostBeard"
MAX_REPOS = 20
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

    repos = [r for r in repos if not r.get('fork', False)]
    repos.sort(key=lambda r: r['stargazers_count'], reverse=True)
    return repos[:MAX_REPOS]

def generate_repo_list(repos):
    # This creates a text block for each repo instead of a table row
    output = ""
    
    for r in repos:
        name = r['name']
        url = r['html_url']
        desc = r['description'] or "No description provided."
        stars = r['stargazers_count']
        forks = r['forks_count']
        
        # Clean description text
        desc = desc.replace("\n", " ")
        if len(desc) > 120: desc = desc[:117] + "..."

        # FORMAT:
        # **[Repo Name](link)**
        # Description text
        # ⭐ 12   🍴 4
        
        # We use <br> to force single-line breaks for a tighter "card" feel
        entry = (
            f"**[{name}]({url})**<br>"
            f"{desc}<br>"
            f"⭐ {stars} &emsp; 🍴 {forks}"
            f"\n\n" # Extra spacing between items
        )
        output += entry
        
    return output

def update_readme(new_content):
    if not os.path.exists(README_PATH):
        print("README.md not found!")
        return

    with open(README_PATH, 'r', encoding='utf-8') as f:
        content = f.read()

    start_pos = content.find(START_MARKER)
    end_pos = content.find(END_MARKER)

    if start_pos == -1 or end_pos == -1:
        print(f"Error: Markers '{START_MARKER}' and '{END_MARKER}' not found in README.")
        return

    pre_content = content[:start_pos + len(START_MARKER)]
    post_content = content[end_pos:]
    
    new_content = f"{pre_content}\n{new_content}\n{post_content}"
    
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
            # Note: We are calling the new list generator function here
            repo_list = generate_repo_list(top_repos)
            update_readme(repo_list)
    else:
        print("GITHUB_TOKEN not found.")
